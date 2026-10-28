using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>
/// 把 skill 包复制进进程拥有的工作区，副本不依赖平台的单轮缓存租约。
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
            // 此处仍拒绝路径分隔符，防止目标逃逸出 skills 目录。
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
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyDirectory(skill.Path, target);
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
            foreach (string file in Directory.EnumerateFiles(skill.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(skill.Path, file) + "\0"));
                await using FileStream content = File.OpenRead(file);
                hash.AppendData(await SHA256.HashDataAsync(content, ct).ConfigureAwait(false));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
