using System.IO.Compression;
using Microsoft.Agents.AI;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

/// <summary>
/// Validates uploaded Skill packages, extracts official frontmatter metadata, and
/// safely reads or materializes package files. It never executes package scripts.
/// </summary>
public static class AgentSkillPackageArchive
{
    public const int MaxFileCount = 128;
    public const int MaxExpandedBytes = 4 * 1024 * 1024;

    public static AgentSkillPackageMetadata Inspect(
        byte[] content,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SkillPackageFile> files = ReadZipFiles(content, cancellationToken);
        return InspectSkillFiles(files);
    }

    public static AgentSkillPackageMetadata InspectMarkdown(
        byte[] content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InspectSkillFiles([new SkillPackageFile("SKILL.md", content)]);
    }

    public static AgentSkillPackageMetadata InspectFiles(
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InspectSkillFiles(files);
    }

    public static IReadOnlyList<SkillPackageFile> ReadZipFiles(
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var files = new List<SkillPackageFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith('/'))
            {
                continue;
            }

            if (!IsSafeRelativePath(normalized) || !paths.Add(normalized))
            {
                throw new InvalidOperationException("Skill package contains an unsafe or duplicate path.");
            }
            if (files.Count >= MaxFileCount)
            {
                throw new InvalidOperationException(
                    $"Skill package cannot contain more than {MaxFileCount} files.");
            }

            using Stream source = entry.Open();
            using var buffer = new MemoryStream();
            CopyWithLimit(source, buffer, ref expandedBytes);
            files.Add(new SkillPackageFile(normalized, buffer.ToArray()));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("Skill package is empty.");
        }

        return files;
    }

    public static void Materialize(
        IEnumerable<SkillPackageStorageFile> files,
        Func<string, byte[]> readFile,
        string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (SkillPackageStorageFile file in files)
        {
            if (!IsSafeRelativePath(file.RelativePath))
            {
                throw new InvalidOperationException("Stored Skill package contains an unsafe path.");
            }

            string target = Path.GetFullPath(Path.Combine(destination, file.RelativePath));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stored Skill package escapes its destination.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, readFile(file.ObjectKey));
        }
    }

    private static AgentSkillPackageMetadata InspectSkillFiles(
        IReadOnlyList<SkillPackageFile> files)
    {
        SkillPackageFile[] skillFiles = files
            .Where(file => string.Equals(Path.GetFileName(file.RelativePath), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (skillFiles.Length != 1)
        {
            throw new InvalidOperationException("Each Skill package must contain exactly one SKILL.md.");
        }

        AgentSkillFrontmatter frontmatter = ReadFrontmatter(skillFiles[0].Content);
        int resourceCount = files.Count(file => HasPathSegment(file.RelativePath, "resources"));
        return new AgentSkillPackageMetadata(
            frontmatter.Name,
            frontmatter.Description,
            1,
            resourceCount);
    }

    private static AgentSkillFrontmatter ReadFrontmatter(byte[] content)
    {
        string[] lines = System.Text.Encoding.UTF8.GetString(content).Split('\n');
        int start = Array.FindIndex(lines, line => line.TrimStart('\uFEFF').Trim() == "---");
        if (start < 0)
        {
            throw new InvalidOperationException("SKILL.md must start with YAML frontmatter.");
        }

        string? name = null;
        string? description = null;
        string? compatibility = null;
        bool closed = false;
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line == "---")
            {
                closed = true;
                break;
            }
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            if (key.Equals("compatibility", StringComparison.OrdinalIgnoreCase)) compatibility = value;
        }

        if (!closed || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("SKILL.md frontmatter requires name and description.");
        }
        return new AgentSkillFrontmatter(name, description, compatibility ?? string.Empty);
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || Path.IsPathRooted(path))
            return false;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment != "." && segment != ".." && !segment.Contains('\0'));
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static void CopyWithLimit(
        Stream source,
        Stream destination,
        ref int expandedBytes)
    {
        byte[] buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            expandedBytes = checked(expandedBytes + read);
            if (expandedBytes > MaxExpandedBytes)
            {
                throw new InvalidOperationException(
                    $"Expanded Skill package exceeds the {MaxExpandedBytes} byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }
}

public sealed record AgentSkillPackageMetadata(
    string Name,
    string Description,
    int SkillCount,
    int ResourceCount);

public sealed record SkillPackageFile(string RelativePath, byte[] Content);
