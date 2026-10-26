using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.Acp.Processes;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>解析后的进程启动规格，可直接作为 AcpProcess 的命令与参数。</summary>
/// <param name="ShellFallback">true 表示经 cmd.exe 转发 npm shell shim，仅 Windows 兜底路径使用。</param>
/// <param name="Source">实际命中的文件路径，用于日志。</param>
internal sealed record OpenCodeLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool ShellFallback = false,
    string? Source = null);

/// <summary>
/// OpenCode 进程启动的单点：先解析可执行文件（显式路径优先，其次 PATH 直接命中与 npm
/// 安装布局——包内 bin、平台可选包、默认全局根，Windows 最后经 cmd.exe 转发 shell shim 兜底），
/// 再按本轮调用生成受控启动配置与环境白名单。
/// </summary>
internal static class OpenCodeProcess
{
    public static OpenCodeLaunchSpec Resolve(OpenCodeOptions options)
    {
        bool windows = OperatingSystem.IsWindows();
        return ResolveCore(
            options.Executable,
            options.Arguments,
            SplitPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, windows),
            windows,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
    }

    internal static OpenCodeLaunchSpec ResolveCore(
        string configured,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> pathEntries,
        bool windows,
        string npmDefaultRoot)
    {
        string name = string.IsNullOrWhiteSpace(configured) ? "opencode" : configured.Trim();
        if (ContainsSeparator(name))
        {
            if (File.Exists(name))
            {
                return new(name, arguments, Source: name);
            }
            throw new AgentException(
                AgentErrorCode.ConfigurationError,
                $"OpenCode executable '{name}' does not exist. Fix the OpenCode:Executable setting.");
        }
        if (windows)
        {
            foreach (string dir in pathEntries)
            {
                foreach (string candidate in WindowsCandidates(dir, name))
                {
                    if (File.Exists(candidate))
                    {
                        return new(candidate, arguments, Source: candidate);
                    }
                }
            }
            // npm 默认全局根（%APPDATA%\npm）不一定在 PATH 上，单独兜一次。
            foreach (string candidate in WindowsCandidates(npmDefaultRoot, name))
            {
                if (File.Exists(candidate))
                {
                    return new(candidate, arguments, Source: candidate);
                }
            }
            // npm 未来版本若只装 shell shim（.cmd/.bat），经 cmd.exe 转发保住可用性。
            foreach (string dir in pathEntries)
            {
                foreach (string extension in new[] { ".cmd", ".bat" })
                {
                    string shim = Path.Combine(dir, name + extension);
                    if (File.Exists(shim))
                    {
                        // /s 语义下 cmd 会剥掉最外层引号，整个命令串必须作为单个参数传入。
                        string command = string.Join(' ', [Quote(shim), .. arguments.Select(Quote)]);
                        return new("cmd.exe", ["/d", "/s", "/c", command], ShellFallback: true, Source: shim);
                    }
                }
            }
        }
        else
        {
            foreach (string dir in pathEntries)
            {
                foreach (string candidate in UnixCandidates(dir, name))
                {
                    if (File.Exists(candidate))
                    {
                        return new(candidate, arguments, Source: candidate);
                    }
                }
            }
        }
        throw new AgentException(
            AgentErrorCode.DependencyUnavailable,
            $"OpenCode executable '{name}' was not found on PATH or in npm install roots. "
            + "Install opencode (npm install -g opencode-ai) or set OpenCode:Executable to an absolute path.");
    }

    private static IEnumerable<string> WindowsCandidates(string dir, string name)
    {
        string executable = Path.Combine(dir, name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");
        yield return executable;
        string stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^".exe".Length]
            : name;
        // npm 自定义全局前缀（如 D:\nodejs）会把前缀目录放进 PATH，
        // 包体与平台二进制位于其 node_modules 下，无法靠扩展名规则命中。
        yield return Path.Combine(dir, "node_modules", "opencode-ai", "bin", stem + ".exe");
        yield return Path.Combine(dir, "node_modules", "opencode-windows-x64", "bin", stem + ".exe");
        yield return Path.Combine(dir, "node_modules", "opencode-windows-x64-baseline", "bin", stem + ".exe");
    }

    private static IEnumerable<string> UnixCandidates(string dir, string name)
    {
        yield return Path.Combine(dir, name);
        // 前缀 bin 目录不在 PATH 时（nvm、项目内 tools 目录），按 node_modules 布局探测。
        yield return Path.Combine(dir, "node_modules", "opencode-ai", "bin", name);
        yield return Path.Combine(dir, "node_modules", ".bin", name);
    }

    private static bool ContainsSeparator(string value)
    {
        return value.Contains('/') || value.Contains('\\') || Path.IsPathRooted(value);
    }

    internal static List<string> SplitPath(string pathVariable, bool windows)
    {
        List<string> dirs = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in pathVariable.Split(windows ? ';' : ':'))
        {
            string dir = entry.Trim().Trim('"');
            if (dir.Length > 0 && seen.Add(dir))
            {
                dirs.Add(dir);
            }
        }
        return dirs;
    }

    private static string Quote(string value)
    {
        return value.Contains(' ') ? "\"" + value + "\"" : value;
    }

    /// <summary>
    /// 从继承的 PATH 中剔除 Windows 商店别名目录（%LOCALAPPDATA%\Microsoft\WindowsApps）。
    /// 别名是 AppModel 激活的交互会话设施：在受控子进程环境（无 LOCALAPPDATA、USERPROFILE
    /// 被重定向）下按名解析到别名会挂死进程而非报错，且别名目录掩盖了 PATH 中靠后的真实
    /// 安装（如 python.org 安装管理器的 AppData\Local\Python\bin）。非 Windows 或不含该
    /// 目录时原样返回。
    /// </summary>
    internal static string FilterWindowsStoreAliases(string pathValue)
    {
        if (!OperatingSystem.IsWindows())
        {
            return pathValue;
        }
        string aliasDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        List<string> kept = [];
        foreach (string entry in pathValue.Split(';'))
        {
            string trimmed = entry.Trim().Trim('"');
            bool isAlias = trimmed.Length > 0
                && (string.Equals(trimmed, aliasDir, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed.TrimEnd('\\') + "\\", aliasDir + "\\", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "%LOCALAPPDATA%\\Microsoft\\WindowsApps", StringComparison.OrdinalIgnoreCase));
            if (!isAlias)
            {
                kept.Add(entry);
            }
        }
        return string.Join(';', kept);
    }

    internal static AcpProcessOptions Create(
        OpenCodeLaunchSpec launch, RuntimeInvocation invocation, IReadOnlyList<string> allowedNativeTools, string home)
    {
        Dictionary<string, string> environment = SharedEnvironment(invocation, allowedNativeTools);
        // 进程主目录与轮次工作区分离：进程保活跨轮次复用时，轮次工作区（ACP 会话 cwd）仍可立即回收。
        foreach (string name in StateVariables)
        {
            string path = Path.Combine(home, ".state", name);
            Directory.CreateDirectory(path);
            environment[name] = path;
        }
        return new AcpProcessOptions(launch.FileName, launch.Arguments)
        {
            WorkingDirectory = home,
            EnvironmentMode = AcpEnvironmentMode.AllowList,
            Environment = environment
        };
    }

    private static readonly string[] StateVariables =
        ["HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "OPENCODE_CONFIG_DIR"];

    /// <summary>
    /// 宿主环境透传白名单：PATH/PATHEXT/COMSPEC/SystemRoot/WINDIR 供进程运行——缺 PATHEXT 时
    /// 按名查找（如 python -> python.exe）失败，Windows PowerShell/where 表现为"命令不存在"。
    /// Node TLS 覆盖（NODE_TLS_REJECT_UNAUTHORIZED 跳过证书校验、NODE_EXTRA_CA_CERTS 追加
    /// 自签名 CA）仅在宿主显式设置时透传，用于自签名证书的模型端点；未设置不进入子进程环境。
    /// </summary>
    private static readonly string[] HostPassthroughVariables =
        ["PATH", "PATHEXT", "COMSPEC", "SystemRoot", "WINDIR", "NODE_TLS_REJECT_UNAUTHORIZED", "NODE_EXTRA_CA_CERTS"];

    /// <summary>
    /// 进程复用身份：实际可执行文件、参数与除进程私有状态目录外的受控环境（含模型端点、
    /// 密钥、指令与工具放行策略，含宿主放行的原生工具）完全一致才允许复用；状态目录路径不参与身份。
    /// </summary>
    internal static string Identity(OpenCodeLaunchSpec launch, RuntimeInvocation invocation, IReadOnlyList<string> allowedNativeTools)
    {
        Dictionary<string, string> environment = SharedEnvironment(invocation, allowedNativeTools);
        StringBuilder identity = new();
        identity.Append(launch.FileName).Append('\0');
        foreach (string argument in launch.Arguments)
        {
            identity.Append(argument).Append('\0');
        }
        foreach (KeyValuePair<string, string> pair in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            identity.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    private static Dictionary<string, string> SharedEnvironment(RuntimeInvocation invocation, IReadOnlyList<string> allowedNativeTools)
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
        // skill 经工作区物化后由原生 skill 工具按需加载，SKILL.md 内容不再注入提示词。
        if ((invocation.Skills?.Count ?? 0) > 0)
        {
            permission["skill"] = "allow";
        }
        // 宿主显式放行的原生工具（如 read/edit/write）：与 "*": "deny" 并存，只有放行项
        // 可用；把对应主机访问面交给 harness 进程是宿主的知情选择，默认不放开。
        foreach (string tool in allowedNativeTools)
        {
            permission[tool] = "allow";
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
        foreach (string name in HostPassthroughVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = name == "PATH" ? FilterWindowsStoreAliases(value) : value;
            }
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
        return environment;
    }
}
