using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Processes;

namespace OpenAgent.Runtime;

internal static class OpenCodeLaunch
{
    internal static AcpProcessOptions Create(OpenCodeOptions options, RuntimeInvocation invocation, string workspace)
    {
        LlmConfig model = invocation.Profile.Model;
        string keyReference = Guid.NewGuid().ToString("N");
        int contextTokens = model.ContextTokens > 0 ? model.ContextTokens : 128000;
        string package = model.Format switch
        {
            ApiFormat.OpenAIChatCompletions => "@ai-sdk/openai-compatible",
            ApiFormat.OpenAIResponses => "@ai-sdk/openai",
            ApiFormat.AnthropicMessages => "@ai-sdk/anthropic",
            _ => throw new ArgumentOutOfRangeException(nameof(model.Format))
        };
        // 原生工具默认全拒绝：只放行平台桥工具（openagent_*）与按 Agent 配置
        // 透传的外部 MCP server 工具（harness 以 server 名作为工具名前缀）。
        JsonObject permission = new() { ["*"] = "deny", ["openagent_*"] = "allow" };
        foreach (string server in (invocation.McpServers ?? []).Select(server => server.Name.ToLowerInvariant()).Distinct())
        {
            permission[server + "_*"] = "allow";
            permission["mcp_" + server + "_*"] = "allow";
        }
        JsonObject config = new()
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = "openagent/" + model.ModelId,
            ["default_agent"] = "openagent",
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["snapshot"] = false,
            ["enabled_providers"] = new JsonArray("openagent"),
            ["permission"] = permission,
            ["agent"] = new JsonObject
            {
                ["openagent"] = new JsonObject
                {
                    ["mode"] = "primary",
                    ["prompt"] = invocation.Instructions,
                    ["steps"] = invocation.Profile.Config.MaxTurns > 0 ? invocation.Profile.Config.MaxTurns : AgentConfig.DefaultMaxTurns,
                    ["temperature"] = model.Temperature
                }
            },
            ["provider"] = new JsonObject
            {
                ["openagent"] = new JsonObject
                {
                    ["npm"] = package,
                    ["name"] = "OpenAgent selected model",
                    ["options"] = new JsonObject
                    {
                        ["baseURL"] = model.Endpoint,
                        ["apiKey"] = keyReference
                    },
                    ["models"] = new JsonObject
                    {
                        [model.ModelId] = new JsonObject
                        {
                            ["name"] = model.ModelId,
                            ["limit"] = new JsonObject { ["context"] = contextTokens, ["output"] = Math.Max(1, Math.Min(16384, contextTokens / 4)) }
                        }
                    }
                }
            }
        };
        // 只给子进程环境白名单，不继承服务的数据库口令、云凭据或用户 HOME 配置。
        Dictionary<string, string> environment = [];
        foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }
        foreach (string name in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "OPENCODE_CONFIG_DIR" })
        {
            string path = Path.Combine(workspace, ".state", name);
            Directory.CreateDirectory(path);
            environment[name] = path;
        }
        environment["OPENAGENT_MODEL_API_KEY"] = model.ApiKey;
        // OpenCode 在解析 JSON 之前展开 {file:...}/{env:...}。转义租户文本里的花括号，
        // 防止 Agent 指令/模型字段被当作宿主配置宏；只还原本轮随机占位的密钥引用。
        TextEncoderSettings characters = new(UnicodeRanges.All);
        characters.ForbidCharacters('{', '}');
        JsonSerializerOptions configJson = new()
        {
            Encoder = JavaScriptEncoder.Create(characters)
        };
        environment["OPENCODE_CONFIG_CONTENT"] = config.ToJsonString(configJson).Replace(
            JsonSerializer.Serialize(keyReference), JsonSerializer.Serialize("{env:OPENAGENT_MODEL_API_KEY}"), StringComparison.Ordinal);
        environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        environment["OPENCODE_DISABLE_SHARE"] = "true";
        return new AcpProcessOptions(options.Executable, options.Arguments)
        {
            WorkingDirectory = workspace,
            EnvironmentMode = AcpEnvironmentMode.AllowList,
            Environment = environment
        };
    }
}
