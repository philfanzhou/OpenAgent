using OpenAgent.Contracts.Security;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Core.Execution;

internal static class AcpLaunch
{
    internal static string Workspace(AcpOptions options, RuntimeInvocation invocation)
    {
        AcpHarness? harness = options.Harnesses.Find(item => item.Id == invocation.Profile.Config.Runtime.Harness);
        if (harness?.WorkingDirectory is { } directory)
        {
            if (!Directory.Exists(directory))
                throw new AgentException(AgentErrorCode.InvalidRequest, "The configured ACP working directory does not exist.");
            return Path.GetFullPath(directory);
        }
        string coordinate = JsonSerializer.Serialize(new
        {
            invocation.Turn.TenantId,
            invocation.Turn.UserId,
            invocation.Turn.ConversationId,
            invocation.Profile.Config.Runtime.Harness
        });
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coordinate))).ToLowerInvariant();
        string path = Path.Combine(Path.GetFullPath(options.WorkspaceRoot), key);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static ProcessStartInfo Create(AcpHarness harness, string workspace, RuntimeInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(harness.Command))
            throw new AgentException(AgentErrorCode.InvalidRequest, "The ACP harness command is not configured.");
        var start = new ProcessStartInfo(harness.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workspace,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Clear();
        foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR", "TMPDIR", "TEMP", "TMP", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        string home = Path.Combine(workspace, ".harness-home");
        if (harness.EnvironmentMode != "local")
            Directory.CreateDirectory(home);
        start.Environment["HOME"] = home;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, "config");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(home, "data");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(home, "cache");
        if (harness.EnvironmentMode == "local")
        {
            foreach (string name in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "CLAUDE_CONFIG_DIR" })
            {
                start.Environment.Remove(name);
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
            }
        }
        foreach (string name in harness.InheritedEnvironmentVariables)
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        foreach (string argument in harness.Arguments)
            start.ArgumentList.Add(argument.Replace("{workspace}", workspace, StringComparison.Ordinal));
        foreach ((string name, string value) in harness.Environment)
            start.Environment[name] = value;
        if (harness.ModelEnvironmentVariable is { } variable && harness.Model is { } nativeModel)
            start.Environment[variable] = nativeModel;
        if (harness.OpenCode)
        {
            start.Environment["OPENCODE_CONFIG_CONTENT"] = OpenCodeConfig(invocation);
            start.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        }
        return start;
    }

    private static string OpenCodeConfig(RuntimeInvocation invocation)
    {
        LlmConfig model = invocation.Profile.Model;
        string sdk = model.Format switch
        {
            ApiFormat.OpenAIChatCompletions => "@ai-sdk/openai-compatible",
            ApiFormat.OpenAIResponses => "@ai-sdk/openai",
            ApiFormat.AnthropicMessages => "@ai-sdk/anthropic",
            _ => throw new AgentException(AgentErrorCode.InvalidRequest, "The model API format is not supported by the OpenCode adapter.")
        };
        return JsonSerializer.Serialize(new
        {
            model = $"matrix/{model.ModelId}",
            default_agent = "matrix",
            share = "disabled",
            autoupdate = false,
            enabled_providers = new[] { "matrix" },
            provider = new Dictionary<string, object>
            {
                ["matrix"] = new
                {
                    npm = sdk,
                    name = "Agent.Matrix",
                    options = new { baseURL = model.Endpoint, apiKey = model.ApiKey },
                    models = new Dictionary<string, object>
                    {
                        [model.ModelId] = new
                        {
                            name = model.ModelId,
                            limit = new { context = model.ContextTokens, output = Math.Min(8192, Math.Max(1, model.ContextTokens / 2)) }
                        }
                    }
                }
            },
            agent = new Dictionary<string, object>
            {
                ["matrix"] = new
                {
                    mode = "primary",
                    prompt = invocation.Profile.Config.Instructions,
                    temperature = model.Temperature,
                    steps = invocation.Profile.Config.MaxTurns > 0 ? invocation.Profile.Config.MaxTurns : AgentConfig.DefaultMaxTurns
                }
            },
            permission = new Dictionary<string, string>
            {
                ["*"] = "ask",
                ["bash"] = invocation.Profile.Config.CodeExecution.Enabled ? "ask" : "deny",
                ["task"] = "deny",
                ["skill"] = "deny",
                ["external_directory"] = "deny"
            }
        });
    }
}
