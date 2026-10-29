using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Files.Services;

/// <summary>
/// 未配置对象存储时的本地磁盘缺省实现；元数据仍由仓储（PostgreSQL）管理。
/// 生产配置 FileAssets:ObjectStorage 后由 Engine.Host 替换为 S3 实现。
/// </summary>
internal sealed class LocalFileObjectStore : IFileObjectStore
{
    private readonly string _root;

    internal LocalFileObjectStore(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Storage root must be absolute.", nameof(root));
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task<FileObjectReference> WriteAsync(FileObjectWriteRequest request, Stream content, CancellationToken cancellationToken)
    {
        string partition = FileObjectTenantScope.CreatePartition(request.TenantId);
        string owner = request.Scope == FileObjectScope.Tenant ? string.Empty
            : $"users/{FileObjectTenantScope.CreatePartition(request.UserId)}/";
        string key = $"local/tenants/{partition}/{owner}{Guid.NewGuid():N}";
        string path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path);
            return new FileObjectReference { ObjectKey = key };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Resolve(objectKey), cancellationToken);

    public async Task<byte[]> ReadAsync(string objectKey, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        await using FileStream input = new(Resolve(objectKey), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (input.Length > maxBytes) throw new AgentException(AgentErrorCode.InvalidRequest, "File object exceeds the configured byte limit.");
        using MemoryStream output = new();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Resolve(objectKey));
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        string path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Object key escapes local storage.");
        return path;
    }
}
