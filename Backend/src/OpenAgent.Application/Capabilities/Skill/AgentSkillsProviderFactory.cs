using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Capabilities.Security;
using OpenAgent.Application.Tooling.Abstractions;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// Materializes configured Agent Skills packages into cached directories and
/// exposes scoped platform tools for skill resources. The package bytes remain
/// in object storage; the process-level cache holds verified copies keyed by
/// content hash for the lifetime of the process, and turns hold leases so
/// active packages are never evicted.
/// </summary>
internal sealed class AgentSkillsProviderFactory(
    IFileObjectStore objectStore,
    ISkillCatalog catalog,
    AgentAuthorizationGate authorization,
    SkillPackageCache? cache = null) : IAgentFeatureFactory
{
    private readonly SkillPackageCache _cache = cache ?? SkillPackageCache.Shared;

    async Task<AgentFeature> IAgentFeatureFactory.CreateAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        AgentSkillsRuntime runtime = await CreateRuntimeAsync(agentId, config, user, cancellationToken).ConfigureAwait(false);
        return new AgentFeature
        {
            Skills = runtime.Skills,
            Tools = runtime.Tools,
            Resource = runtime
        };
    }

    internal async Task<AgentSkillsRuntime> CreateRuntimeAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        var skills = new List<RuntimeSkill>();
        List<SkillPackageCache.Lease> leases = [];

        IReadOnlyList<SkillInstanceConfig> instances = await ResolveInstancesAsync(
            config.Skills,
            catalog,
            user.TenantId,
            cancellationToken).ConfigureAwait(false);
        foreach (SkillInstanceConfig instance in instances.Where(IsEnabledObjectPackage))
        {
            if (!IsSelected(config.Skills, instance)
                || !await authorization.IsAvailableAsync(
                    agentId,
                    AgentResourceType.Skill,
                    string.IsNullOrWhiteSpace(instance.Id) ? instance.Name : instance.Id,
                    user,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            string directoryName = GetPackageDirectoryName(instance.Name) ?? Guid.NewGuid().ToString("N");
            // 包目录来自进程级内容缓存：命中时零下载；未命中时在 staging 内下载 +
            // 校验后原子提交。租约在轮次存活期内保护目录不被淘汰；无包哈希的记录
            // 退回一次性目录，随租约释放删除。
            SkillPackageCache.Lease lease = await _cache.GetOrMaterializeAsync(
                instance.TenantId!,
                instance.ObjectKey!,
                instance.Sha256,
                (staging, token) => MaterializeAsync(instance, staging, token),
                cancellationToken).ConfigureAwait(false);
            leases.Add(lease);
            skills.Add(new RuntimeSkill(directoryName, lease.Path) { ContentHash = instance.Sha256 });
        }

        if (skills.Count == 0)
        {
            foreach (SkillPackageCache.Lease lease in leases)
            {
                lease.Dispose();
            }
            return AgentSkillsRuntime.Empty;
        }

        Dictionary<string, string> roots = skills.ToDictionary(
            skill => skill.Name, skill => skill.Path, StringComparer.Ordinal);
        List<AITool> tools = [CreateSkillFileReader(roots)];

        return new AgentSkillsRuntime(skills, tools, leases);
    }

    /// <summary>
    /// Skill 包内文件只经该工具暴露给模型：读取被限制在物化包目录内，
    /// 不授予 harness 任何宿主文件系统访问。
    /// </summary>
    private static AIFunction CreateSkillFileReader(Dictionary<string, string> roots) =>
        AIFunctionFactory.Create(new SkillFileTarget(roots).ReadAsync,
            name: "read_skill_file",
            description: "Read a file bundled with an enabled agent skill. 'skill' is the skill name; 'path' is relative to the skill package root.");

    private sealed class SkillFileTarget(Dictionary<string, string> roots)
    {
        public async Task<string?> ReadAsync(string skill, string path, CancellationToken cancellationToken = default)
        {
            ResolveInPackage(roots, skill, path, out string full);
            return await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveInPackage(Dictionary<string, string> roots, string skill, string path, out string full)
    {
        if (!roots.TryGetValue(skill, out string? root))
        {
            throw new InvalidOperationException($"Skill '{skill}' is not enabled for this agent.");
        }
        full = Path.GetFullPath(Path.Combine(root, path));
        string rooted = Path.GetFullPath(root);
        if (!full.StartsWith(rooted + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, rooted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Skill path escapes the skill package.");
        }
        return root;
    }

    private static bool IsEnabledObjectPackage(SkillInstanceConfig instance) =>
        instance.Enabled
        && string.Equals(instance.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(instance.ObjectKey);

    /// <summary>
    /// The MAF file-skills source requires the parent directory of SKILL.md to be
    /// named exactly after the skill name, so materialize each package under a
    /// directory derived from <see cref="SkillInstanceConfig.Name"/> (which is taken
    /// from the SKILL.md frontmatter at upload). Falls back to a random directory
    /// when the name is missing or not directory-safe.
    /// </summary>
    private static string? GetPackageDirectoryName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$")
            ? name
            : null;

    private static bool IsSelected(SkillsConfig config, SkillInstanceConfig instance) =>
        config.EnabledSkills.Count == 0
        || config.EnabledSkills.Contains(instance.Id, StringComparer.OrdinalIgnoreCase)
        || config.EnabledSkills.Contains(instance.Name, StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<SkillInstanceConfig>> ResolveInstancesAsync(
        SkillsConfig config,
        ISkillCatalog catalog,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return [];
        }

        if (config.EnabledSkills.Count == 0)
        {
            return config.Instances
                .Where(instance => string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal))
                .ToList()
                .AsReadOnly();
        }

        var resolved = new List<SkillInstanceConfig>();
        foreach (string id in config.EnabledSkills)
        {
            SkillInstanceConfig? skill = await catalog.GetAsync(
                tenantId,
                id,
                cancellationToken).ConfigureAwait(false)
                ?? config.Instances.FirstOrDefault(item =>
                    string.Equals(item.TenantId, tenantId, StringComparison.Ordinal)
                    && string.Equals(item.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase)
                    && (
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase)));
            if (skill != null) resolved.Add(skill);
        }
        return resolved.AsReadOnly();
    }

    private async Task MaterializeAsync(
        SkillInstanceConfig instance,
        string packagePath,
        CancellationToken cancellationToken)
    {
        EnsureTenantSharedObjectKey(instance.ObjectKey!, instance.TenantId);
        byte[] storedContent = await objectStore.ReadAsync(instance.ObjectKey!, cancellationToken).ConfigureAwait(false);
        VerifyHash(instance, storedContent);

        SkillPackageStorageIndex index;
        if (string.Equals(instance.PackageFormat, "directory", StringComparison.OrdinalIgnoreCase))
        {
            index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(storedContent)
                ?? throw new InvalidOperationException($"Skill package '{instance.Id}' has an invalid storage index.");
            if (!string.Equals(index.TenantId, instance.TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Skill package '{instance.Id}' belongs to another tenant.");
            }
            // 上传归一化之前存的记录可能还带着 zip 的顶层包裹目录（SKILL.md 嵌套）：
            // 物化时剥掉，使 SKILL.md 落在包根，符合 OpenCode 发现与 read_skill_file 的约定。
            AgentSkillPackageArchive.NormalizeStorageLayout(index.Files);
        }
        else
        {
            // Keep already-installed ZIP records readable during migration. New uploads
            // always use the directory format below.
            IReadOnlyList<SkillPackageFile> files = AgentSkillPackageArchive.ReadZipFiles(storedContent, cancellationToken);
            index = new SkillPackageStorageIndex
            {
                Files = files.Select((file, index) => new SkillPackageStorageFile
                {
                    RelativePath = file.RelativePath,
                    ObjectKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Sha256 = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant()
                }).ToList()
            };
            WriteFiles(index, files, packagePath);
            return;
        }

        foreach (SkillPackageStorageFile file in index.Files)
        {
            EnsureTenantSharedObjectKey(file.ObjectKey, instance.TenantId);
            byte[] content = await objectStore.ReadAsync(file.ObjectKey, cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Skill package '{instance.Id}' file '{file.RelativePath}' failed its SHA-256 integrity check.");
            }

            AgentSkillPackageArchive.Materialize(
                [new SkillPackageStorageFile
                {
                    RelativePath = file.RelativePath,
                    ObjectKey = file.ObjectKey,
                    Sha256 = file.Sha256
                }],
                key => string.Equals(key, file.ObjectKey, StringComparison.Ordinal) ? content : [],
                packagePath);
        }
    }

    private static void WriteFiles(
        SkillPackageStorageIndex index,
        IReadOnlyList<SkillPackageFile> files,
        string packagePath)
    {
        var byKey = files
            .Select((file, index) => (Key: index.ToString(System.Globalization.CultureInfo.InvariantCulture), File: file))
            .ToDictionary(item => item.Key, item => item.File, StringComparer.Ordinal);
        AgentSkillPackageArchive.Materialize(
            index.Files,
            key => byKey[key].Content,
            packagePath);
    }

    private static void VerifyHash(SkillInstanceConfig instance, byte[] content)
    {
        if (string.IsNullOrWhiteSpace(instance.Sha256)) return;

        string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actual, instance.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Skill package '{instance.Id}' failed its SHA-256 integrity check.");
        }
    }

    private static void EnsureTenantSharedObjectKey(string objectKey, string tenantId)
    {
        if (!FileObjectTenantScope.ContainsTenantSharedPartition(objectKey, tenantId))
        {
            throw new InvalidOperationException("Skill object storage key is outside the tenant-shared partition.");
        }
    }
}
