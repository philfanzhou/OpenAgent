using System.Text;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Client;

/// <summary>显式启用的文件处理器。默认只读，路径校验不替代 OS 沙箱，也不能消除 TOCTOU。</summary>
public sealed class RootedFileSystem(Func<string, IReadOnlyList<string>> roots, bool allowWrite = false, int maxBytes = 1024 * 1024)
{
    public async Task<ReadFileResponse> ReadAsync(ReadFileRequest request, CancellationToken ct = default)
    {
        if (request.Line == 0 || request.Limit == 0)
        {
            throw new ArgumentException("Line and limit must be positive.");
        }

        string path = Authorize(request.SessionId, request.Path);
        // 先限制输入文件，避免单行文件在 ReadLine 前占用无界内存。
        if (new FileInfo(path).Length > maxBytes)
        {
            throw new AcpRpcException(10002, "File input limit.");
        }

        using StreamReader reader = File.OpenText(path);
        StringBuilder text = new();
        uint index = 1, taken = 0;
        int bytes = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (index++ < (request.Line ?? 1))
            {
                continue;
            }

            if (taken++ >= (request.Limit ?? uint.MaxValue))
            {
                break;
            }

            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > maxBytes)
            {
                throw new AcpRpcException(10002, "File output limit.");
            }

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(line);
        }
        return new(text.ToString());
    }
    public async Task<Empty> WriteAsync(WriteFileRequest request, CancellationToken ct = default)
    {
        if (!allowWrite)
        {
            throw new AcpRpcException(-32000, "File writes are disabled.");
        }

        string path = Authorize(request.SessionId, request.Path);
        if (Encoding.UTF8.GetByteCount(request.Content) > maxBytes)
        {
            throw new AcpRpcException(10002, "File input limit.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, request.Content, ct).ConfigureAwait(false);
        return new();
    }
    private string Authorize(string sessionId, string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("File path must be absolute.");
        }

        string resolved = Resolve(path);
        foreach (string root in roots(sessionId))
        {
            string relative = Path.GetRelativePath(Resolve(root), resolved);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return resolved;
            }
        }
        throw new AcpRpcException(-32000, "File path is outside authorized roots.");
    }
    private static string Resolve(string path)
    {
        // 逐段解析符号链接，不能仅对用户给出的字符串做 StartsWith。
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new IOException("Unresolved link.");
            }
        }
        return current;
    }
}
