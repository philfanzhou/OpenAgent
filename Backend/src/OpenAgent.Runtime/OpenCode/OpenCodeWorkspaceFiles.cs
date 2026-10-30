using System.Security.Cryptography;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Filesystem side of a turn lease; platform services retain authorization and asset storage.</summary>
internal sealed class OpenCodeWorkspaceFiles(string workspace) : IRuntimeWorkspaceFiles
{
    public async Task<RuntimeWorkspaceFile> ImportAsync(RuntimeFile file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(file.FileId) || file.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw InvalidPath();
        }
        string name = SafeName(file.FileName);
        string relative = $"inputs/{file.FileId}/{name}";
        string directory = Path.Combine(workspace, "inputs", file.FileId);
        EnsureDirectory(workspace);
        EnsureDirectory(Path.Combine(workspace, "inputs"));
        EnsureDirectory(directory);
        EnsureDirectory(Path.Combine(workspace, "outputs"));
        string target = Path.Combine(directory, name);
        RejectExistingLink(target);
        if (data.Length != file.Length || !Convert.ToHexStringLower(SHA256.HashData(data.Span)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "The input file failed its integrity check.");
        }
        if (File.Exists(target))
        {
            await using FileStream existing = new(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
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
