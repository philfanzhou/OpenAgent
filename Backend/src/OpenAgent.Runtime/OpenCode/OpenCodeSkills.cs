using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Copies authorized skill packages into the turn workspace without following links.</summary>
internal static class OpenCodeSkills
{
    internal static void Materialize(string workspace, IReadOnlyList<RuntimeSkill>? skills, CancellationToken cancellationToken = default)
    {
        if (skills is null || skills.Count == 0)
        {
            return;
        }
        foreach (RuntimeSkill skill in skills)
        {
            // 名称由工厂侧按 opencode 约定（^[a-z0-9]+(-[a-z0-9]+)*$）或 GUID 生成；
            // 此处仍拒绝路径分隔符，防止链接目标逃逸出 skills 目录。
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
            string target = Path.Combine(workspace, ".opencode", "skills", name);
            CopyDirectory(skill.Path, target, cancellationToken);
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
