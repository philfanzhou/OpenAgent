using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.OpenCode;

namespace OpenAgent.Runtime.Pi;

internal static class PiProcessConfiguration
{
    private const string ModelKeyVariable = "OPENAGENT_PI_MODEL_KEY";

    internal static AcpProcessOptions Launch(PiOptions options, RuntimeInvocation invocation, string workspace, string agentDirectory)
    {
        var environment = new Dictionary<string, string>
        {
            [ModelKeyVariable] = invocation.Profile.Model.ApiKey,
            ["PI_CODING_AGENT_DIR"] = agentDirectory,
            ["HOME"] = agentDirectory,
            ["USERPROFILE"] = agentDirectory
        };
        foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR", "NODE_EXTRA_CA_CERTS" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                environment[name] = value;

        return new AcpProcessOptions(options.Executable, ["--agent-dir", agentDirectory,
            "--session-dir", Path.Combine(agentDirectory, "sessions"), "--no-delegation", "--quiet-startup",
            "--model", "openagent/" + invocation.Profile.Model.ModelId])
        {
            WorkingDirectory = workspace,
            EnvironmentMode = AcpEnvironmentMode.AllowList,
            Environment = environment
        };
    }

    internal static async Task WriteAsync(PiOptions options, RuntimeInvocation invocation, string workspace, string directory, CancellationToken ct)
    {
        LlmConfig model = invocation.Profile.Model;
        string api = model.Format switch
        {
            ApiFormat.OpenAIChatCompletions => "openai-completions",
            ApiFormat.OpenAIResponses => "openai-responses",
            ApiFormat.AnthropicMessages => "anthropic-messages",
            _ => throw new ArgumentOutOfRangeException(nameof(model.Format))
        };
        int context = model.ContextTokens > 0 ? model.ContextTokens : 128_000;
        string modelConfig = JsonSerializer.Serialize(new
        {
            providers = new
            {
                openagent = new
                {
                    baseUrl = model.Endpoint,
                    apiKey = ModelKeyVariable,
                    api,
                    models = new[] { new { id = model.ModelId, name = model.ModelId, reasoning = false,
                        input = model.Modality == ModelModality.Multimodal ? new[] { "text", "image" } : ["text"],
                        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                        contextWindow = context, maxTokens = Math.Max(1, Math.Min(16_384, context / 4)) } }
                }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "models.json"), modelConfig, ct).ConfigureAwait(false);
        string extensionDir = Path.Combine(directory, "extensions");
        OpenCodeWorkspace.CreatePrivateDirectory(extensionDir);
        string extension = $$"""
            export default function (pi) {
              const allowed = new Set({{JsonSerializer.Serialize(options.AllowedNativeTools)}});
              const instructions = {{JsonSerializer.Serialize(invocation.Instructions)}};
              pi.on('tool_call', (event) => {
                if (allowed.has(event.toolName) || event.toolName === 'tool_search' || event.toolName.startsWith('mcp__')) return;
                return {block: true, reason: 'Tool is not enabled by the OpenAgent host'};
              });
              pi.on('before_agent_start', (event) => ({systemPrompt: event.systemPrompt + '\n' + instructions}));
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(extensionDir, "openagent.ts"), extension, ct).ConfigureAwait(false);
        if (invocation.Skills?.Count > 0)
        {
            string settings = JsonSerializer.Serialize(new { skills = new[] { Path.Combine(workspace, ".opencode", "skills") } });
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), settings, ct).ConfigureAwait(false);
        }
    }
}
