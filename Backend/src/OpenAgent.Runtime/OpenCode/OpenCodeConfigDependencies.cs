using System.Text.Json;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>让 OpenCode 的后台配置依赖安装成为空操作。</summary>
/// <remarks>
/// OpenCode 1.x 加载配置时，会对 XDG 配置目录、OPENCODE_CONFIG_DIR 和工作区里的 .opencode 目录
/// 各启动一次后台依赖安装（添加 <c>@opencode-ai/plugin</c>，供 JS/TS 配置文件 import），没有开关。
/// 安装会访问 npm registry 并解包数千个文件：进程在轮次结束后仍占用约 1.3–1.6 核持续约 30 秒，
/// 并在进程主目录留下上百 MB 缓存。平台只通过 OPENCODE_CONFIG_CONTENT 提供 JSON 配置，不加载
/// JS/TS 配置插件，用不到这个包。
/// 上游安装前只检查「node_modules 目录存在，且 package-lock.json 的根依赖已登记全部声明依赖」；
/// 预置满足检查的空壳即可跳过安装。空壳不含真实包：将来上游若确实要 import 该包，会走按需安装，
/// 行为退回到本类引入之前。
/// </remarks>
internal static class OpenCodeConfigDependencies
{
    /// <summary>上游固定安装的配置插件包；这里只登记名称，不提供包内容。</summary>
    internal const string PackageName = "@opencode-ai/plugin";

    // 版本号不参与上游的跳过判断；0.0.0 表示「未实际安装」。
    private const string PlaceholderVersion = "0.0.0";

    private static readonly string Manifest = JsonSerializer.Serialize(new
    {
        dependencies = new Dictionary<string, string> { [PackageName] = PlaceholderVersion }
    });

    private static readonly string LockFile = JsonSerializer.Serialize(new
    {
        name = "opencode",
        lockfileVersion = 3,
        requires = true,
        packages = new Dictionary<string, object>
        {
            [""] = new { dependencies = new Dictionary<string, string> { [PackageName] = PlaceholderVersion } }
        }
    });

    /// <summary>在配置目录预置空壳；已有内容（含此前真实安装的结果）保持不变。</summary>
    internal static void Seed(string directory)
    {
        EnsureDirectory(directory);
        EnsureDirectory(Path.Combine(directory, "node_modules"));
        WriteIfAbsent(Path.Combine(directory, "package.json"), Manifest);
        WriteIfAbsent(Path.Combine(directory, "package-lock.json"), LockFile);
    }

    // 目录可能已由 OpenCode 以默认 umask 创建（例如升级前已提交的会话）。父目录已经是进程私有的 0700，
    // 这里只拒绝链接，不收紧已有目录的权限位，避免旧会话的追问因目录权限而失败。
    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            OpenCodeWorkspace.RejectLink(new DirectoryInfo(path));
            return;
        }
        OpenCodeWorkspace.CreatePrivateDirectory(path);
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            OpenCodeWorkspace.RejectLink(new FileInfo(path));
            return;
        }
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using FileStream stream = new(path, options);
        using StreamWriter writer = new(stream);
        writer.Write(content);
    }
}
