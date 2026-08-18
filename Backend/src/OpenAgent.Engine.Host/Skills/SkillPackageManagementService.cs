using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Host.Skills;

internal sealed class SkillPackageManagementService(
    AgentConfigManagementService agentConfigs,
    IFileObjectStore objectStore,
    ILogger<SkillPackageManagementService> logger,
    ISkillCatalogStore? skillCatalog = null)
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
        AgentConfigEntity? entity = await agentConfigs.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            return SkillPackageInstallResult.NotFound();
        }

        SkillPackageUploadResult uploaded = await UploadAsync(
            tenantId, userId, fileName, mediaType, package, cancellationToken, publishCatalog: false).ConfigureAwait(false);
        SkillInstanceConfig instance = uploaded.Skill;

        int index = entity.Config.Skills.Instances.FindIndex(item =>
            string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase));
        SkillInstanceConfig? previousInstance = index >= 0 ? entity.Config.Skills.Instances[index] : null;
        string? previousObjectKey = previousInstance?.ObjectKey;
        if (index >= 0)
        {
            entity.Config.Skills.Instances[index] = instance;
        }
        else
        {
            entity.Config.Skills.Instances.Add(instance);
        }

        entity.Config.Skills.EnabledSkills.RemoveAll(item =>
            string.Equals(item, instance.Id, StringComparison.OrdinalIgnoreCase));
        entity.Config.Skills.EnabledSkills.Add(instance.Id);
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
            await DeletePackageBestEffortAsync(instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);
            throw;
        }
        if (saved == null)
        {
            await DeletePackageBestEffortAsync(instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);
            return SkillPackageInstallResult.Conflict();
        }

        if (!string.IsNullOrWhiteSpace(previousObjectKey)
            && !string.Equals(previousObjectKey, instance.ObjectKey, StringComparison.Ordinal))
        {
            await DeletePackageBestEffortAsync(previousObjectKey, previousInstance?.PackageFormat).ConfigureAwait(false);
        }

        return SkillPackageInstallResult.Installed(instance, saved.CurrentVersion);
    }

    internal async Task<SkillPackageUploadResult> UploadAsync(
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        CancellationToken cancellationToken,
        bool publishCatalog = true)
    {
        byte[] content = await ReadPackageAsync(package, cancellationToken).ConfigureAwait(false);
        bool isMarkdown = string.Equals(Path.GetExtension(fileName), ".md", StringComparison.OrdinalIgnoreCase);
        if (!isMarkdown && !string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Skill upload must be a .zip package or a single .md file.");

        IReadOnlyList<SkillPackageFile> files = isMarkdown
            ? [new SkillPackageFile("SKILL.md", content)]
            : AgentSkillPackageArchive.ReadZipFiles(content, cancellationToken);
        if (files.Sum(item => item.Content.LongLength) > MaxPackageBytes)
            throw new InvalidOperationException("Expanded Skill package exceeds the 4 MB limit.");

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.InspectFiles(files, cancellationToken);
        SkillInstanceConfig? previous = !publishCatalog || skillCatalog == null
            ? null
            : await skillCatalog.GetAsync(metadata.Name, cancellationToken).ConfigureAwait(false);
        string packageId = $"skill-{Guid.NewGuid():N}";
        const string packagePrefixRoot = "skill-packages";
        var storedKeys = new List<string>();
        FileObjectReference stored;
        string storedIndexHash;
        try
        {
            var storedFiles = new List<SkillPackageStorageFile>(files.Count);
            for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                SkillPackageFile file = files[fileIndex];
                string fileHash = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant();
                await using var input = new MemoryStream(file.Content, writable: false);
                FileObjectReference fileObject = await objectStore.WriteAsync(
                    new FileObjectWriteRequest
                    {
                        FileId = $"{packageId}-{fileIndex:D4}",
                        TenantId = tenantId,
                        UserId = userId,
                        FileName = Path.GetFileName(file.RelativePath),
                        MediaType = mediaType,
                        Sha256 = fileHash,
                        ObjectKeyPrefix = $"{packagePrefixRoot}/{packageId}/{Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/') ?? string.Empty}"
                    },
                    input,
                    cancellationToken).ConfigureAwait(false);
                storedKeys.Add(fileObject.ObjectKey);
                storedFiles.Add(new SkillPackageStorageFile
                {
                    RelativePath = file.RelativePath,
                    ObjectKey = fileObject.ObjectKey,
                    Sha256 = fileHash
                });
            }

            byte[] indexContent = JsonSerializer.SerializeToUtf8Bytes(new SkillPackageStorageIndex { Files = storedFiles });
            storedIndexHash = Convert.ToHexString(SHA256.HashData(indexContent)).ToLowerInvariant();
            await using var indexStream = new MemoryStream(indexContent, writable: false);
            stored = await objectStore.WriteAsync(
                new FileObjectWriteRequest
                {
                    FileId = packageId,
                    TenantId = tenantId,
                    UserId = userId,
                    FileName = $"{packageId}.json",
                    MediaType = "application/json",
                    Sha256 = storedIndexHash,
                    ObjectKeyPrefix = $"{packagePrefixRoot}/{packageId}"
                },
                indexStream,
                cancellationToken).ConfigureAwait(false);
            storedKeys.Add(stored.ObjectKey);
        }
        catch
        {
            foreach (string objectKey in storedKeys)
                await DeleteObjectBestEffortAsync(objectKey).ConfigureAwait(false);
            throw;
        }

        var instance = new SkillInstanceConfig
        {
            Id = metadata.Name,
            Name = metadata.Name,
            Enabled = true,
            Description = metadata.Description,
            Source = "ObjectStorage",
            SourceId = metadata.Name,
            PackageFileName = fileName,
            PackageFormat = "directory",
            ObjectKey = stored.ObjectKey,
            Sha256 = storedIndexHash,
            ResourceCount = metadata.ResourceCount
        };
        if (publishCatalog && skillCatalog != null)
        {
            try
            {
                await skillCatalog.PublishAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DeletePackageBestEffortAsync(instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);
                throw;
            }

            if (previous?.ObjectKey != null && !string.Equals(previous.ObjectKey, instance.ObjectKey, StringComparison.Ordinal))
                await DeletePackageBestEffortAsync(previous.ObjectKey, previous.PackageFormat).ConfigureAwait(false);
        }

        return new SkillPackageUploadResult(instance);
    }

    internal async Task<bool> DeleteCatalogAsync(
        string skillId,
        CancellationToken cancellationToken)
    {
        if (skillCatalog == null) return false;
        SkillInstanceConfig? skill = await skillCatalog.GetAsync(skillId, cancellationToken).ConfigureAwait(false);
        if (skill == null) return false;
        await DeletePackageBestEffortAsync(skill.ObjectKey, skill.PackageFormat).ConfigureAwait(false);
        await skillCatalog.RemoveAsync(skillId, cancellationToken).ConfigureAwait(false);
        return true;
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

        await DeletePackageBestEffortAsync(instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);

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

                IReadOnlyList<SkillPackageFile> files = string.Equals(instance.PackageFormat, "directory", StringComparison.OrdinalIgnoreCase)
                    ? await ReadStoredFilesAsync(content, cancellationToken).ConfigureAwait(false)
                    : AgentSkillPackageArchive.ReadZipFiles(content, cancellationToken);
                AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.InspectFiles(files, cancellationToken);
                if (!string.Equals(metadata.Name, instance.Id, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(metadata.Name, instance.Name, StringComparison.OrdinalIgnoreCase))
                {
                    invalid.Add(instance.Id);
                    continue;
                }
                verified.Add(instance.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
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

    private async Task DeletePackageBestEffortAsync(string? objectKey, string? packageFormat)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return;

        if (string.Equals(packageFormat, "directory", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] indexContent = await objectStore.ReadAsync(objectKey, CancellationToken.None).ConfigureAwait(false);
                SkillPackageStorageIndex? index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(indexContent);
                if (index != null)
                {
                    foreach (SkillPackageStorageFile file in index.Files)
                        await DeleteObjectBestEffortAsync(file.ObjectKey).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to read Skill storage index {ObjectKey}; deleting index only.", objectKey);
            }
        }

        await DeleteObjectBestEffortAsync(objectKey).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SkillPackageFile>> ReadStoredFilesAsync(
        byte[] indexContent,
        CancellationToken cancellationToken)
    {
        SkillPackageStorageIndex index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(indexContent)
            ?? throw new InvalidOperationException("Skill storage index is invalid.");
        var files = new List<SkillPackageFile>(index.Files.Count);
        foreach (SkillPackageStorageFile file in index.Files)
        {
            byte[] content = await objectStore.ReadAsync(file.ObjectKey, cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Skill file '{file.RelativePath}' failed its SHA-256 integrity check.");
            files.Add(new SkillPackageFile(file.RelativePath, content));
        }
        return files;
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

internal sealed record SkillPackageUploadResult(SkillInstanceConfig Skill);

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
