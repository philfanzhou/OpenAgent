using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Skill;

/// <summary>
/// Materializes configured Agent Skills packages and hands them to the official
/// Microsoft Agent Framework provider. The package bytes remain in object storage;
/// the temporary directory only exists for the lifetime of one agent execution.
/// </summary>
internal sealed class AgentSkillsProviderFactory(
    IFileObjectStore objectStore,
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
            foreach (SkillInstanceConfig instance in config.Instances.Where(IsEnabledObjectPackage))
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

                string packagePath = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
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
                        // MAF requires an explicit runner for scripts. OpenAgent does
                        // not execute uploaded code in the host process, so scripts are
                        // not advertised until a sandboxed runner is introduced.
                        ScriptFilter = _ => false
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
        instance.Enabled && !string.IsNullOrWhiteSpace(instance.ObjectKey);

    private static bool IsSelected(SkillsConfig config, SkillInstanceConfig instance) =>
        config.EnabledSkills.Count == 0
        || config.EnabledSkills.Contains(instance.Id, StringComparer.OrdinalIgnoreCase)
        || config.EnabledSkills.Contains(instance.Name, StringComparer.OrdinalIgnoreCase);

    private async Task MaterializeAsync(
        SkillInstanceConfig instance,
        string packagePath,
        CancellationToken cancellationToken)
    {
        byte[] storedContent = await objectStore.ReadAsync(instance.ObjectKey!, cancellationToken).ConfigureAwait(false);
        VerifyHash(instance, storedContent);

        SkillPackageStorageIndex index;
        if (string.Equals(instance.PackageFormat, "directory", StringComparison.OrdinalIgnoreCase))
        {
            index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(storedContent)
                ?? throw new InvalidOperationException($"Skill package '{instance.Id}' has an invalid storage index.");
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
