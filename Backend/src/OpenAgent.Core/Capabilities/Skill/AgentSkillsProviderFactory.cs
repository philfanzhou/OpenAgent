using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Skill;

/// <summary>
/// Materializes configured Agent Skills packages and hands them to the official
/// Microsoft Agent Framework provider. The package bytes remain in object storage;
/// the temporary directory only exists for the lifetime of one agent execution.
/// </summary>
internal sealed class AgentSkillsProviderFactory(
    IFileObjectStore objectStore,
    ISkillCatalog catalog,
    AgentAuthorizationGate authorization,
    ILoggerFactory loggerFactory)
{
    internal async Task<AgentSkillsRuntime> CreateAsync(
        string agentId,
        SkillsConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "openagent-skills", Guid.NewGuid().ToString("N"));
        var packagePaths = new List<string>();
        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(temporaryRoot);
            IReadOnlyList<SkillInstanceConfig> instances = await ResolveInstancesAsync(
                config,
                catalog,
                user.TenantId,
                cancellationToken).ConfigureAwait(false);
            foreach (SkillInstanceConfig instance in instances.Where(IsEnabledObjectPackage))
            {
                if (!IsSelected(config, instance)
                    || !await authorization.IsAvailableAsync(
                        agentId,
                        AgentResourceType.Skill,
                        string.IsNullOrWhiteSpace(instance.Id) ? instance.Name : instance.Id,
                        user,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                string packagePath = Path.Combine(temporaryRoot, GetPackageDirectoryName(instance.Name) ?? Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(packagePath);
                await MaterializeAsync(instance, packagePath, cancellationToken).ConfigureAwait(false);
                packagePaths.Add(packagePath);

                if (!string.IsNullOrWhiteSpace(instance.Name))
                {
                    allowedNames.Add(instance.Name);
                }
                if (!string.IsNullOrWhiteSpace(instance.Id))
                {
                    allowedNames.Add(instance.Id);
                }
            }

            if (packagePaths.Count == 0)
            {
                Directory.Delete(temporaryRoot, recursive: true);
                return AgentSkillsRuntime.Empty;
            }

            AgentSkillsProvider provider = new AgentSkillsProviderBuilder()
                .UseFileSkills(
                    packagePaths,
                    new AgentFileSkillsSourceOptions
                    {
                        ScriptFilter = _ => false
                    })
                .UseFileScriptRunner((_, _, _, _, _) =>
                    throw new InvalidOperationException(
                        "Skill scripts are disabled until an isolated script runner is configured."))
                // The current chat contract cannot round-trip MAF approval requests.
                // Loading Skill instructions and reading resources remain constrained by
                // the Agent binding and OpenAgent authorization checks above.
                .UseOptions(options =>
                {
                    options.DisableLoadSkillApproval = true;
                    options.DisableReadSkillResourceApproval = true;
                    options.IncludeDetailedErrors = false;
                })
                .UseFilter((skill, _) =>
                    allowedNames.Count == 0
                    || allowedNames.Contains(skill.Frontmatter.Name))
                .UseLoggerFactory(loggerFactory)
                .Build();

            return new AgentSkillsRuntime(provider, temporaryRoot);
        }
        catch
        {
            TryDelete(temporaryRoot);
            throw;
        }
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

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort; the original exception is more useful to the caller.
        }
    }
}

internal sealed class AgentSkillsRuntime : IAsyncDisposable
{
    internal static AgentSkillsRuntime Empty { get; } = new(null, null);

    internal AgentSkillsRuntime(AgentSkillsProvider? provider, string? temporaryRoot)
    {
        Provider = provider;
        TemporaryRoot = temporaryRoot;
    }

    internal AgentSkillsProvider? Provider { get; }
    private string? TemporaryRoot { get; }

    public ValueTask DisposeAsync()
    {
        Provider?.Dispose();
        try
        {
            if (!string.IsNullOrWhiteSpace(TemporaryRoot) && Directory.Exists(TemporaryRoot))
            {
                Directory.Delete(TemporaryRoot, recursive: true);
            }
        }
        catch
        {
            // Temporary package cleanup must not hide the agent result.
        }
        return ValueTask.CompletedTask;
    }
}
