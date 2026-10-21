using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Runtime;

/// <summary>解析后的进程启动规格，可直接作为 AcpProcess 的命令与参数。</summary>
/// <param name="ShellFallback">true 表示经 cmd.exe 转发 npm shell shim，仅 Windows 兜底路径使用。</param>
/// <param name="Source">实际命中的文件路径，用于日志。</param>
internal sealed record OpenCodeLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool ShellFallback = false,
    string? Source = null);

/// <summary>
/// 解析 OpenCode 可执行文件：显式路径优先，其次 PATH 直接命中与 npm 安装布局
/// （包内 bin、平台可选包、默认全局根），Windows 最后经 cmd.exe 转发 shell shim 兜底。
/// Linux/macOS 的 npm 启动器本身可执行，PATH 命中即返回。
/// </summary>
internal static class OpenCodeExecutable
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
            + "Install the pinned version (npm install -g opencode-ai@1.18.35) or set OpenCode:Executable to an absolute path.");
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
}
