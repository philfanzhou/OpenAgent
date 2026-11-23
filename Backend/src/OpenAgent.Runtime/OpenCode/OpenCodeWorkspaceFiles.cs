using System.Security.Cryptography;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Filesystem side of a turn lease; platform services retain authorization and asset storage.</summary>
internal sealed class OpenCodeWorkspaceFiles : IRuntimeWorkspaceFiles
{
    private readonly string _workspace;

    internal OpenCodeWorkspaceFiles(string workspace)
    {
        _workspace = workspace;
        EnsureDirectory(workspace);
        EnsureDirectory(Path.Combine(workspace, "inputs"));
        EnsureDirectory(Path.Combine(workspace, "outputs"));
    }

    public async Task<RuntimeWorkspaceFile> ImportAsync(RuntimeFile file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(file.FileId) || file.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw InvalidPath();
        }
        string name = SafeName(file.FileName);
        string relative = $"inputs/{file.FileId}/{name}";
        string directory = Path.Combine(_workspace, "inputs", file.FileId);
        EnsureDirectory(_workspace);
        EnsureDirectory(Path.Combine(_workspace, "inputs"));
        EnsureDirectory(directory);
        EnsureDirectory(Path.Combine(_workspace, "outputs"));
        string target = Path.Combine(directory, name);
        RejectExistingLink(target);
        if (data.Length != file.Length || !Convert.ToHexStringLower(SHA256.HashData(data.Span)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "The input file failed its integrity check.");
        }
        if (File.Exists(target))
        {
            await using FileStream existing = WorkspaceFileHandle.Open(target);
            if (existing.Length == file.Length
                && Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false))
                    .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(file, relative);
            }
        }
        string temporary = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectExistingLink(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return new(file, relative);
    }

    public async Task<RuntimeOutputFile> SnapshotAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "The output byte limit must be positive.");
        }
        string path = OutputPath(relativePath);
        string snapshots = Path.Combine(Path.GetDirectoryName(_workspace)!, ".file-exchange");
        EnsureDirectory(snapshots);
        FileStream snapshot = new(Path.Combine(snapshots, Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await using FileStream source = WorkspaceFileHandle.Open(path);
            long length = source.Length;
            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (length == 0 || length > maxBytes)
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, "The output file is empty or exceeds the file size limit.");
            }
            byte[] buffer = new byte[65536];
            while (true)
            {
                int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                if (count > maxBytes - snapshot.Length)
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The output file exceeds the file size limit.");
                }
                await snapshot.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            snapshot.Position = 0;
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(snapshot, cancellationToken).ConfigureAwait(false));
            source.Position = 0;
            using IncrementalHash verifier = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long verifiedLength = 0;
            while (true)
            {
                int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                verifiedLength += count;
                if (verifiedLength > length)
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The output file changed while being exported.");
                }
                verifier.AppendData(buffer, 0, count);
            }
            string verified = Convert.ToHexStringLower(verifier.GetHashAndReset());
            _ = OutputPath(relativePath);
            if (length != snapshot.Length || source.Length != length || File.GetLastWriteTimeUtc(path) != modified || hash != verified)
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, "The output file changed while being exported. Finish writing it before retrying.");
            }
            snapshot.Position = 0;
            return new RuntimeOutputFile(Path.GetRelativePath(_workspace, path).Replace('\\', '/'), Path.GetFileName(path), length, hash, snapshot);
        }
        catch
        {
            await snapshot.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string OutputPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl))
        {
            throw InvalidPath();
        }
        string[] segments = relative.Split('/');
        if (segments.Length < 2 || segments[0] != "outputs" || segments.Any(segment => segment is "" or "." or ".."))
        {
            throw InvalidPath();
        }
        OpenCodeWorkspace.RejectLink(new DirectoryInfo(_workspace));
        string path = _workspace;
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
            RejectExistingLink(path);
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw InvalidPath();
            }
        }
        return path;
    }

    private static string SafeName(string name)
    {
        string safe = new(name.Select(character => char.IsControl(character)
            || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length > 120)
        {
            string extension = Path.GetExtension(safe);
            safe = safe[..80] + (extension.Length <= 20 ? extension : string.Empty);
        }
        return string.IsNullOrEmpty(safe) || safe is "." or ".." ? "file" : safe;
    }

    private static void EnsureDirectory(string path) => OpenCodeWorkspace.CreatePrivateDirectory(path);

    private static void RejectExistingLink(string path)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidPath();
        }
    }

    private static AgentException InvalidPath() => new(AgentErrorCode.InvalidRequest, "The workspace file path is not allowed.");
}
