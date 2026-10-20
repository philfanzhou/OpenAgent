using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime;

/// <summary>
/// 把本轮物化好的 skill 包链接进工作区 .opencode/skills/&lt;name&gt;，交由
/// OpenCode 原生 skill 工具按需发现/加载。优先符号链接（零拷贝、源目录只读）；
/// Windows 未开启符号链接权限或跨卷等失败场景退化为目录复制。
/// </summary>
internal static class OpenCodeSkills
{
    internal static void Materialize(string workspace, IReadOnlyList<RuntimeSkill>? skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return;
        }
        foreach (RuntimeSkill skill in skills)
        {
            // 名称由工厂侧按 opencode 约定（^[a-z0-9]+(-[a-z0-9]+)*$）或 GUID 生成；
            // 此处仍拒绝路径分隔符，防止链接目标逃逸出 skills 目录。
            if (string.IsNullOrWhiteSpace(skill.Name)
                || skill.Name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0
                || !Path.IsPathRooted(skill.Path))
            {
                throw new InvalidOperationException("Invalid skill package reference.");
            }
            if (!Directory.Exists(skill.Path))
            {
                throw new InvalidOperationException($"Skill package directory is missing: {skill.Name}");
            }
            string link = Path.Combine(workspace, ".opencode", "skills", skill.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            try
            {
                Directory.CreateSymbolicLink(link, skill.Path);
            }
            catch (IOException)
            {
                CopyDirectory(skill.Path, link);
            }
            catch (UnauthorizedAccessException)
            {
                CopyDirectory(skill.Path, link);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
