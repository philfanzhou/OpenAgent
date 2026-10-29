using System.IO.Compression;

namespace OpenAgent.Contracts.Skills;

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

    /// <summary>
    /// Reads a zip package into its canonical layout. Consumers resolve SKILL.md at
    /// the package root (OpenCode native discovery links the package directory as
    /// .opencode/skills/&lt;name&gt;; read_skill_file roots paths at the package), but
    /// folder-compression tools commonly emit zips that wrap everything in one more
    /// directory — the wrapper holding SKILL.md is stripped from every relative path.
    /// </summary>
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

        return NormalizeLayout(files);
    }

    /// <summary>
    /// Rewrites relative paths so SKILL.md lands at the package root: when SKILL.md
    /// sits inside a wrapper directory, that prefix is stripped from every entry.
    /// Files outside the wrapper are rejected — silently rehoming them would change
    /// the layout the skill author described. A no-op when SKILL.md is already at
    /// the root or missing (missing files stay InspectSkillFiles' error).
    /// </summary>
    public static IReadOnlyList<SkillPackageFile> NormalizeLayout(IReadOnlyList<SkillPackageFile> files)
    {
        string? prefix = GetWrapperPrefix(files.Select(file => file.RelativePath));
        if (prefix is null)
        {
            return files;
        }
        List<SkillPackageFile> normalized = new(files.Count);
        foreach (SkillPackageFile file in files)
        {
            normalized.Add(file with { RelativePath = StripWrapperPrefix(file.RelativePath, prefix) });
        }
        return normalized;
    }

    /// <summary>
    /// The storage-index twin of <see cref="NormalizeLayout"/>: rewrites relative
    /// paths in place (object keys and hashes stay untouched) so already-stored
    /// records uploaded before normalization materialize with SKILL.md at the root.
    /// </summary>
    public static void NormalizeStorageLayout(IReadOnlyList<SkillPackageStorageFile> files)
    {
        string? prefix = GetWrapperPrefix(files.Select(file => file.RelativePath));
        if (prefix is null)
        {
            return;
        }
        foreach (SkillPackageStorageFile file in files)
        {
            file.RelativePath = StripWrapperPrefix(file.RelativePath, prefix);
        }
    }

    private static string StripWrapperPrefix(string relativePath, string prefix)
    {
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Skill package must keep SKILL.md at the root or nest every file under its directory.");
        }
        return relativePath[prefix.Length..];
    }

    /// <summary>The directory holding SKILL.md (with trailing separator), or null when
    /// SKILL.md already sits at the package root or is absent.</summary>
    private static string? GetWrapperPrefix(IEnumerable<string> relativePaths)
    {
        string? skillPath = null;
        foreach (string path in relativePaths)
        {
            if (!string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (skillPath is not null && !string.Equals(skillPath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Each Skill package must contain exactly one SKILL.md.");
            }
            skillPath = path;
        }
        string? directory = skillPath is null ? null : Path.GetDirectoryName(skillPath)?.Replace('\\', '/');
        return string.IsNullOrEmpty(directory) ? null : directory + "/";
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
        return new AgentSkillFrontmatter(name!, description!, compatibility ?? string.Empty);
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
