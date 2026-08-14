using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Config;

namespace OpenAgent.Engine.Host.Skills;

internal sealed class SkillPackageManagementService(
    AgentConfigManagementService agentConfigs,
    IFileObjectStore objectStore,
    ISkillPackageReader packageReader,
    ILogger<SkillPackageManagementService> logger)
{
    internal const long MaxPackageBytes = 4 * 1024 * 1024;

    internal async Task<SkillPackageInstallResult> InstallAsync(
        string agentId,
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        byte[] content = await ReadPackageAsync(package, cancellationToken).ConfigureAwait(false);
        SkillPackageManifest manifest = packageReader.Read(fileName, content);
        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        AgentConfigEntity? entity = await agentConfigs.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            return SkillPackageInstallResult.NotFound();
        }

        await using var input = new MemoryStream(content, writable: false);
        FileObjectReference stored = await objectStore.WriteAsync(
            new FileObjectWriteRequest
            {
                FileId = $"skill-{Guid.NewGuid():N}",
                TenantId = tenantId,
                UserId = userId,
                FileName = fileName,
                MediaType = mediaType,
                Sha256 = sha256
            },
            input,
            cancellationToken).ConfigureAwait(false);
        var instance = new SkillInstanceConfig
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Enabled = true,
            Description = manifest.Description,
            ParametersJsonSchema = manifest.ParametersJsonSchema,
            Type = manifest.Type,
            EndpointUrl = manifest.EndpointUrl,
            Version = manifest.Version,
            Source = "ObjectStorage",
            SourceId = stored.ObjectKey,
            PackageFileName = fileName,
            PackageFormat = packageReader.GetFormat(fileName),
            ObjectKey = stored.ObjectKey,
            Sha256 = sha256
        };

        int index = entity.Config.Skills.Instances.FindIndex(item =>
            string.Equals(item.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
        string? previousObjectKey = index >= 0
            ? entity.Config.Skills.Instances[index].ObjectKey
            : null;
        if (index >= 0)
        {
            entity.Config.Skills.Instances[index] = instance;
        }
        else
        {
            entity.Config.Skills.Instances.Add(instance);
        }

        entity.Config.Skills.EnabledSkills.RemoveAll(item =>
            string.Equals(item, manifest.Id, StringComparison.OrdinalIgnoreCase));
        entity.Config.Skills.EnabledSkills.Add(manifest.Id);
        AgentConfigEntity? saved;
        try
        {
            saved = await agentConfigs.SaveAsync(
                agentId,
                entity,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteObjectBestEffortAsync(stored.ObjectKey).ConfigureAwait(false);
            throw;
        }
        if (saved == null)
        {
            await DeleteObjectBestEffortAsync(stored.ObjectKey).ConfigureAwait(false);
            return SkillPackageInstallResult.Conflict();
        }

        if (!string.IsNullOrWhiteSpace(previousObjectKey)
            && !string.Equals(previousObjectKey, stored.ObjectKey, StringComparison.Ordinal))
        {
            await DeleteObjectBestEffortAsync(previousObjectKey).ConfigureAwait(false);
        }

        return SkillPackageInstallResult.Installed(instance, saved.CurrentVersion);
    }

    internal async Task<SkillPackageDeleteResult> DeleteAsync(
        string agentId,
        string skillId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        AgentConfigEntity? entity = await agentConfigs.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            return SkillPackageDeleteResult.AgentNotFound;
        }

        SkillInstanceConfig? instance = entity.Config.Skills.Instances.FirstOrDefault(item =>
            string.Equals(item.Id, skillId, StringComparison.OrdinalIgnoreCase));
        if (instance == null)
        {
            return SkillPackageDeleteResult.SkillNotFound;
        }

        entity.Config.Skills.Instances.Remove(instance);
        entity.Config.Skills.EnabledSkills.RemoveAll(item =>
            string.Equals(item, skillId, StringComparison.OrdinalIgnoreCase));
        AgentConfigEntity? saved = await agentConfigs.SaveAsync(
            agentId,
            entity,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        if (saved == null)
        {
            return SkillPackageDeleteResult.Conflict;
        }

        await DeleteObjectBestEffortAsync(instance.ObjectKey).ConfigureAwait(false);

        return SkillPackageDeleteResult.Deleted;
    }

    internal async Task<SkillPackageValidationResult> ValidateAsync(
        SkillsConfig skills,
        CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var verified = new List<string>();
        foreach (SkillInstanceConfig instance in skills.Instances.Where(item => item.Enabled))
        {
            if (string.IsNullOrWhiteSpace(instance.Id) || string.IsNullOrWhiteSpace(instance.Name))
            {
                invalid.Add(string.IsNullOrWhiteSpace(instance.Id) ? instance.Name : instance.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(instance.ObjectKey))
            {
                continue;
            }

            try
            {
                byte[] content = await objectStore.ReadAsync(instance.ObjectKey, cancellationToken).ConfigureAwait(false);
                string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(instance.Sha256)
                    && !string.Equals(instance.Sha256, actual, StringComparison.OrdinalIgnoreCase))
                {
                    invalid.Add(instance.Id);
                    continue;
                }

                packageReader.Read(instance.PackageFileName ?? string.Empty, content);
                verified.Add(instance.Id);
            }
            catch (InvalidOperationException)
            {
                invalid.Add(instance.Id);
            }
        }

        return new SkillPackageValidationResult(
            invalid.Count == 0,
            skills.EnabledSkills.Count,
            skills.Instances.Count,
            verified,
            invalid);
    }

    private static async Task<byte[]> ReadPackageAsync(
        Stream package,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await package.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("Skill package is empty.");
        }

        if (buffer.Length > MaxPackageBytes)
        {
            throw new InvalidOperationException("Skill package exceeds the 4 MB limit.");
        }

        return buffer.ToArray();
    }

    private async Task DeleteObjectBestEffortAsync(string? objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await objectStore.DeleteAsync(objectKey, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < 3)
            {
                logger.LogWarning(exception, "Failed to delete Skill package object {ObjectKey}; retry {Attempt}.", objectKey, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to delete Skill package object {ObjectKey} after retries.", objectKey);
            }
        }
    }
}

internal sealed record SkillPackageInstallResult(
    SkillInstanceConfig? Skill,
    string? CurrentVersion,
    bool AgentExists,
    bool HasConflict)
{
    internal static SkillPackageInstallResult Installed(SkillInstanceConfig skill, string currentVersion) =>
        new(skill, currentVersion, AgentExists: true, HasConflict: false);

    internal static SkillPackageInstallResult NotFound() =>
        new(null, null, AgentExists: false, HasConflict: false);

    internal static SkillPackageInstallResult Conflict() =>
        new(null, null, AgentExists: true, HasConflict: true);
}

internal enum SkillPackageDeleteResult
{
    Deleted,
    AgentNotFound,
    SkillNotFound,
    Conflict
}

internal sealed record SkillPackageValidationResult(
    bool Success,
    int EnabledCount,
    int InstanceCount,
    IReadOnlyList<string> ObjectStorageVerifiedSkills,
    IReadOnlyList<string> InvalidSkills);
