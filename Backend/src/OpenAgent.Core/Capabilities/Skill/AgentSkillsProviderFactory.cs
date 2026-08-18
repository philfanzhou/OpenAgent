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
    ISkillScriptSandbox scriptSandbox,
    ILoggerFactory loggerFactory)
{
    internal async Task<AgentSkillsRuntime> CreateAsync(
        string agentId,
        SkillsConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger<AgentSkillsProviderFactory>();
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "openagent-skills", Guid.NewGuid().ToString("N"));
        var packagePaths = new List<string>();
        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scriptEnabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SkillScriptSandboxStatus sandboxStatus = scriptSandbox.Status;

        try
        {
            Directory.CreateDirectory(temporaryRoot);
            foreach (SkillInstanceConfig instance in ResolveInstances(config, catalog).Where(IsEnabledObjectPackage))
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
                if (instance.AllowScriptExecution && instance.ScriptCount > 0)
                {
                    scriptEnabledNames.Add(instance.Name);
                    scriptEnabledNames.Add(instance.Id);
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
                        AllowedScriptExtensions = sandboxStatus.SupportedExtensions,
                        ScriptFilter = context => IsAllowedScript(
                            context.SkillName,
                            context.RelativeFilePath,
                            sandboxStatus,
                            scriptEnabledNames,
                            logger)
                    })
                .UseFileScriptRunner((skill, script, arguments, _, cancellationToken) =>
                    RunScriptAsync(
                        skill,
                        script,
                        arguments,
                        scriptEnabledNames,
                        cancellationToken))
                // OpenAgent's approval boundary is the admin-only, per-Skill execution
                // switch plus the sandbox policy. The current chat contract cannot
                // round-trip MAF approval requests, so do not expose unusable approval tools.
                .UseOptions(options =>
                {
                    options.DisableLoadSkillApproval = true;
                    options.DisableReadSkillResourceApproval = true;
                    options.DisableRunSkillScriptApproval = true;
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

    private async Task<object?> RunScriptAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IReadOnlySet<string> scriptEnabledNames,
        CancellationToken cancellationToken)
    {
        if (!scriptSandbox.Status.Enabled)
        {
            throw new InvalidOperationException(DisabledSkillScriptSandbox.DisabledMessage);
        }

        string skillRoot = Path.GetFullPath(skill.Path) + Path.DirectorySeparatorChar;
        string scriptPath = Path.GetFullPath(script.FullPath);
        string relativePath = Path.GetRelativePath(skill.Path, scriptPath);
        if (!scriptEnabledNames.Contains(skill.Frontmatter.Name)
            || !scriptPath.StartsWith(skillRoot, StringComparison.Ordinal)
            || !IsScriptPath(relativePath, scriptSandbox.Status.SupportedExtensions))
        {
            throw new InvalidOperationException("Skill script is not authorized by the owning Skill policy.");
        }

        FileInfo scriptFile = new(scriptPath);
        if (!scriptFile.Exists || scriptFile.Length <= 0
            || scriptFile.Length > scriptSandbox.Status.MaxScriptBytes)
        {
            throw new InvalidOperationException("Skill script size is outside the sandbox policy.");
        }

        IReadOnlyList<string> parsedArguments = ParseArguments(arguments);
        byte[] content = await File.ReadAllBytesAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        return await scriptSandbox.ExecuteAsync(
            new SkillScriptExecutionRequest
            {
                SkillName = skill.Frontmatter.Name,
                ScriptName = Path.GetFileName(scriptPath),
                Script = content,
                Arguments = parsedArguments
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ParseArguments(JsonElement? arguments)
    {
        if (arguments == null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }
        if (arguments.Value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Skill script arguments must be a JSON string array.");
        }

        var result = new List<string>();
        foreach (JsonElement argument in arguments.Value.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Skill script arguments must contain only strings.");
            }
            result.Add(argument.GetString() ?? string.Empty);
        }
        return result;
    }

    private static bool IsScriptPath(
        string relativePath,
        IReadOnlyList<string> supportedExtensions)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)
            && supportedExtensions.Contains(
                Path.GetExtension(normalized),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAllowedScript(
        string skillName,
        string relativeFilePath,
        SkillScriptSandboxStatus sandboxStatus,
        IReadOnlySet<string> scriptEnabledNames,
        ILogger logger)
    {
        bool allowed = sandboxStatus.Enabled
            && scriptEnabledNames.Contains(skillName)
            && IsScriptPath(relativeFilePath, sandboxStatus.SupportedExtensions);
        logger.LogDebug(
            "Skill script discovery policy evaluated Skill={SkillName}, Path={RelativeFilePath}, Allowed={Allowed}",
            skillName,
            relativeFilePath,
            allowed);
        return allowed;
    }

    private static bool IsEnabledObjectPackage(SkillInstanceConfig instance) =>
        instance.Enabled && !string.IsNullOrWhiteSpace(instance.ObjectKey);

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

    private static IEnumerable<SkillInstanceConfig> ResolveInstances(
        SkillsConfig config,
        ISkillCatalog catalog)
    {
        if (config.EnabledSkills.Count == 0)
        {
            return config.Instances;
        }

        var resolved = new List<SkillInstanceConfig>();
        foreach (string id in config.EnabledSkills)
        {
            SkillInstanceConfig? skill = catalog.Get(id)
                ?? config.Instances.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase));
            if (skill != null) resolved.Add(skill);
        }
        return resolved;
    }

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
