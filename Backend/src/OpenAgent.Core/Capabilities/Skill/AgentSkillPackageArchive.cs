using System.IO.Compression;
using Microsoft.Agents.AI;

namespace OpenAgent.Core.Capabilities.Skill;

internal static class AgentSkillPackageArchive
{
    internal static AgentSkillPackageMetadata Inspect(
        byte[] content,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "openagent-skill-inspect", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            ExtractZip(content, root);
            cancellationToken.ThrowIfCancellationRequested();
            string[] skillFiles = Directory.GetFiles(root, "SKILL.md", SearchOption.AllDirectories);
            if (skillFiles.Length == 0)
            {
                throw new InvalidOperationException("Skill package does not contain a valid SKILL.md.");
            }

            AgentSkillFrontmatter frontmatter = ReadFrontmatter(skillFiles[0]);
            return new AgentSkillPackageMetadata(
                frontmatter.Name,
                frontmatter.Description,
                skillFiles.Length);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Do not replace validation errors with best-effort temp cleanup errors.
            }
        }
    }

    private static AgentSkillFrontmatter ReadFrontmatter(string path)
    {
        string[] lines = File.ReadAllLines(path);
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

    internal static void ExtractZip(byte[] content, string destination)
    {
        using var input = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        bool hasSkillFile = false;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith('/'))
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(destination, normalized));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Skill package contains an unsafe path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream source = entry.Open();
            using FileStream output = File.Create(target);
            source.CopyTo(output);
            if (string.Equals(Path.GetFileName(target), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                hasSkillFile = true;
            }
        }

        if (!hasSkillFile)
        {
            throw new InvalidOperationException("Skill package must contain at least one SKILL.md file.");
        }
    }
}

internal sealed record AgentSkillPackageMetadata(
    string Name,
    string Description,
    int SkillCount);
