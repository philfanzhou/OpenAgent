using Microsoft.Extensions.Logging;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Private storage for a trusted service identity; does not isolate processes sharing that identity.</summary>
internal static class OpenCodeWorkspace
{
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows ACLs must be provisioned for the service identity by the operator.
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        RejectLink(new DirectoryInfo(path));
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new IOException("OpenCode storage must be accessible only to the service identity (mode 0700).");
        }
    }

    internal static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("OpenCode storage must not contain symbolic links or reparse points.");
        }
    }

    /// <summary>删除文件或目录（含内容）；成功返回 null，否则返回失败原因。目录项是符号链接时只删除链接本身。</summary>
    internal static Exception? TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error;
        }
    }

    /// <summary>
    /// 已提交的主目录只保留原生数据库（<c>.state/data</c>）与工作区；其余是进程运行期目录
    /// （JIT 映射、缓存、配置依赖桩、临时文件），下次启动时由进程重建，却占已落盘体积的八成以上。
    /// </summary>
    internal static void ReleaseRuntimeState(string home, ILogger logger)
    {
        foreach (string directory in RuntimeDirectories)
        {
            string path = Path.Combine(home, directory);
            if (!Directory.Exists(path)) continue;
            Exception? failure = TryDelete(path);
            if (failure is not null) OpenCodeLog.CleanupFailure(logger, directory, failure.GetType().Name);
        }
    }

    private static readonly string[] RuntimeDirectories =
    [
        .. OpenCodeProcess.StateVariables.Values.Distinct(StringComparer.Ordinal)
            .Where(name => name != "data").Select(name => Path.Combine(".state", name)),
        ".file-exchange"
    ];
}
