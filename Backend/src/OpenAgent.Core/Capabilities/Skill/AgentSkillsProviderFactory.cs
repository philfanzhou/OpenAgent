using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
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

                byte[] content = await objectStore.ReadAsync(instance.ObjectKey!, cancellationToken).ConfigureAwait(false);
                VerifyHash(instance, content);

                string packagePath = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(packagePath);
                AgentSkillPackageArchive.ExtractZip(content, packagePath);
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

    private static void VerifyHash(SkillInstanceConfig instance, byte[] content)
    {
        if (string.IsNullOrWhiteSpace(instance.Sha256))
        {
            return;
        }

        string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
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
