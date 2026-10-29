using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Copies authorized packages into the process-owned workspace without following links.</summary>
internal static class OpenCodeSkills
{
    internal static void Materialize(string workspace, IReadOnlyList<RuntimeSkill>? skills, CancellationToken cancellationToken = default)
    {
        foreach (RuntimeSkill skill in skills ?? [])
        {
            string name = skill.Name ?? string.Empty;
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$")
                || !Path.IsPathRooted(skill.Path))
            {
                throw new InvalidOperationException("Invalid skill package reference.");
            }
            if (!Directory.Exists(skill.Path))
            {
                throw new InvalidOperationException($"Skill package directory is missing: {skill.Name}");
            }
            CopyDirectory(skill.Path, Path.Combine(workspace, ".opencode", "skills", name), cancellationToken);
        }
    }

    internal static async Task<string> FingerprintAsync(IReadOnlyList<RuntimeSkill>? skills, CancellationToken ct)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (RuntimeSkill skill in (skills ?? []).OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(skill.Name + "\0"));
            if (!string.IsNullOrWhiteSpace(skill.ContentHash))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(skill.ContentHash + "\0"));
                continue;
            }
            foreach (FileInfo file in PackageFiles(new DirectoryInfo(skill.Path), ct))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(skill.Path, file.FullName) + "\0"));
                await using FileStream content = file.OpenRead();
                hash.AppendData(await SHA256.HashDataAsync(content, ct).ConfigureAwait(false));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static IEnumerable<FileInfo> PackageFiles(DirectoryInfo directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OpenCodeWorkspace.RejectLink(directory);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            OpenCodeWorkspace.RejectLink(entry);
            if (entry is DirectoryInfo nested)
            {
                foreach (FileInfo file in PackageFiles(nested, ct))
                {
                    yield return file;
                }
            }
            else
            {
                yield return (FileInfo)entry;
            }
        }
    }

    private static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryInfo directory = new(source);
        OpenCodeWorkspace.RejectLink(directory);
        OpenCodeWorkspace.CreatePrivateDirectory(target);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCodeWorkspace.RejectLink(entry);
            string destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo)
            {
                CopyDirectory(entry.FullName, destination, cancellationToken);
            }
            else
            {
                File.Copy(entry.FullName, destination, overwrite: false);
            }
        }
    }
}
