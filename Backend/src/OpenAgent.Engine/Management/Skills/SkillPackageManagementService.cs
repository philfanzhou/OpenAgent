using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;

namespace OpenAgent.Engine.Management.Skills;

internal sealed class SkillPackageManagementService(
    ConfigurationService agentConfigs,
    IFileObjectStore objectStore,
    ILogger<SkillPackageManagementService> logger,
    ISkillCatalogStore? skillCatalog = null) : ISkillPackageManagementService
{
    public async Task<SkillPackageInstallResult> InstallAsync(
        string agentId,
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        AgentConfigEntity? entity = await agentConfigs
            .GetAgentAsync(agentId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (entity == null)
        {
            return SkillPackageInstallResult.NotFound();
        }
        if (!string.IsNullOrWhiteSpace(entity.TenantId)
            && !string.Equals(entity.TenantId, tenantId, StringComparison.Ordinal))
        {
            return SkillPackageInstallResult.TenantMismatch();
        }
        if (string.IsNullOrWhiteSpace(entity.TenantId)
            && (entity.Config.Skills.EnabledSkills.Count > 0 || entity.Config.Skills.Instances.Count > 0))
        {
            return SkillPackageInstallResult.TenantMismatch();
        }
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;

        SkillPackageUploadResult uploaded = await UploadCoreAsync(
            tenantId, userId, fileName, mediaType, package, cancellationToken,
            publishCatalog: false).ConfigureAwait(false);
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
            saved = await agentConfigs.SaveAgentAsync(
                agentId,
                tenantId,
                entity,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeletePackageBestEffortAsync(tenantId, instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);
            throw;
        }
        if (saved == null)
        {
            await DeletePackageBestEffortAsync(tenantId, instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);
            return SkillPackageInstallResult.Conflict();
        }

        if (!string.IsNullOrWhiteSpace(previousObjectKey)
            && !string.Equals(previousObjectKey, instance.ObjectKey, StringComparison.Ordinal))
        {
            await DeletePackageBestEffortAsync(tenantId, previousObjectKey, previousInstance?.PackageFormat).ConfigureAwait(false);
        }

        return SkillPackageInstallResult.Installed(instance, saved.CurrentVersion);
    }

    public Task<SkillPackageUploadResult> UploadAsync(
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        CancellationToken cancellationToken) =>
        UploadCoreAsync(tenantId, userId, fileName, mediaType, package, cancellationToken, publishCatalog: true);

    internal Task<SkillPackageUploadResult> UploadAsync(
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        CancellationToken cancellationToken,
        bool publishCatalog) =>
        UploadCoreAsync(tenantId, userId, fileName, mediaType, package, cancellationToken, publishCatalog);

    private async Task<SkillPackageUploadResult> UploadCoreAsync(
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        CancellationToken cancellationToken,
        bool publishCatalog)
    {
        byte[] content = await ReadPackageAsync(package, cancellationToken).ConfigureAwait(false);
        bool isMarkdown = string.Equals(Path.GetExtension(fileName), ".md", StringComparison.OrdinalIgnoreCase);
        if (!isMarkdown && !string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Skill upload must be a .zip package or a single .md file.");

        IReadOnlyList<SkillPackageFile> files = isMarkdown
            ? [new SkillPackageFile("SKILL.md", content)]
            : AgentSkillPackageArchive.ReadZipFiles(content, cancellationToken);
        if (files.Sum(item => item.Content.LongLength) > ISkillPackageManagementService.MaxPackageBytes)
            throw new InvalidOperationException("Expanded Skill package exceeds the 4 MB limit.");

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.InspectFiles(files, cancellationToken);
        SkillInstanceConfig? previous = !publishCatalog || skillCatalog == null
            ? null
            : await skillCatalog.GetAsync(
                tenantId,
                metadata.Name,
                cancellationToken).ConfigureAwait(false);
        (FileObjectReference stored, string storedIndexHash) = await StorePackageAsync(
            tenantId, userId, mediaType, files, cancellationToken).ConfigureAwait(false);

        var instance = new SkillInstanceConfig
        {
            Id = metadata.Name,
            TenantId = tenantId,
            Name = metadata.Name,
            Enabled = true,
            Description = metadata.Description,
            Type = SkillTypes.AgentSkill,
            Source = SkillSourceTypes.ObjectStorage,
            SourceType = SkillSourceTypes.ObjectStorage,
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
                await DeletePackageBestEffortAsync(tenantId, instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);
                throw;
            }

            if (previous?.ObjectKey != null && !string.Equals(previous.ObjectKey, instance.ObjectKey, StringComparison.Ordinal))
                await DeletePackageBestEffortAsync(tenantId, previous.ObjectKey, previous.PackageFormat).ConfigureAwait(false);
        }

        return new SkillPackageUploadResult(instance);
    }

    /// <summary>
    /// Writes one object per package file plus the storage index object under a
    /// fresh package id. On failure every written object is deleted again.
    /// </summary>
    private async Task<(FileObjectReference IndexObject, string IndexSha256)> StorePackageAsync(
        string tenantId,
        string userId,
        string mediaType,
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken cancellationToken)
    {
        string packageId = $"skill-{Guid.NewGuid():N}";
        const string packagePrefixRoot = "skill-packages";
        var storedKeys = new List<string>();
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
                        Scope = FileObjectScope.Tenant,
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

            byte[] indexContent = JsonSerializer.SerializeToUtf8Bytes(new SkillPackageStorageIndex
            {
                TenantId = tenantId,
                Files = storedFiles
            });
            string storedIndexHash = Convert.ToHexString(SHA256.HashData(indexContent)).ToLowerInvariant();
            await using var indexStream = new MemoryStream(indexContent, writable: false);
            FileObjectReference stored = await objectStore.WriteAsync(
                new FileObjectWriteRequest
                {
                    FileId = packageId,
                    TenantId = tenantId,
                    UserId = userId,
                    Scope = FileObjectScope.Tenant,
                    FileName = $"{packageId}.json",
                    MediaType = "application/json",
                    Sha256 = storedIndexHash,
                    ObjectKeyPrefix = $"{packagePrefixRoot}/{packageId}"
                },
                indexStream,
                cancellationToken).ConfigureAwait(false);
            storedKeys.Add(stored.ObjectKey);
            return (stored, storedIndexHash);
        }
        catch
        {
            foreach (string objectKey in storedKeys)
                await DeleteObjectBestEffortAsync(objectKey).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Replaces the SKILL.md of an existing catalog Skill in place: the package
    /// files are re-stored unchanged under a new package id, so the rest of the
    /// package survives an online description edit. The frontmatter name must
    /// stay the same to keep Agent bindings and the catalog id stable.
    /// </summary>
    public async Task<SkillInstanceConfig?> UpdateMarkdownAsync(
        string tenantId,
        string userId,
        string skillId,
        string markdown,
        CancellationToken cancellationToken)
    {
        if (skillCatalog == null) return null;
        SkillInstanceConfig? skill = await skillCatalog.GetAsync(
            tenantId,
            skillId,
            cancellationToken).ConfigureAwait(false);
        if (skill == null || string.IsNullOrWhiteSpace(skill.ObjectKey)) return null;
        EnsureTenantSharedObjectKey(skill.ObjectKey, tenantId);

        byte[] markdownBytes = System.Text.Encoding.UTF8.GetBytes(markdown);
        AgentSkillPackageMetadata markdownMetadata = AgentSkillPackageArchive.InspectFiles(
            [new SkillPackageFile("SKILL.md", markdownBytes)], cancellationToken);
        if (!string.Equals(markdownMetadata.Name, skill.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(markdownMetadata.Name, skill.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Skill name cannot change when updating the package in place; re-upload a package to rename it.");
        }

        byte[] indexContent = await objectStore.ReadAsync(skill.ObjectKey, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SkillPackageFile> files = string.Equals(skill.PackageFormat, "directory", StringComparison.OrdinalIgnoreCase)
            ? await ReadStoredFilesAsync(tenantId, indexContent, cancellationToken).ConfigureAwait(false)
            : AgentSkillPackageArchive.ReadZipFiles(indexContent, cancellationToken);
        // Directory-format records stored before layout normalization may still carry
        // the zip's wrapper directory; normalize so an online edit also repairs it.
        files = AgentSkillPackageArchive.NormalizeLayout(files);
        if (files.All(file => !string.Equals(Path.GetFileName(file.RelativePath), "SKILL.md", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Skill package '{skillId}' has no SKILL.md to replace.");

        var merged = new List<SkillPackageFile>(files.Count);
        foreach (SkillPackageFile file in files)
        {
            merged.Add(string.Equals(Path.GetFileName(file.RelativePath), "SKILL.md", StringComparison.OrdinalIgnoreCase)
                ? new SkillPackageFile(file.RelativePath, markdownBytes)
                : file);
        }
        if (merged.Sum(file => file.Content.LongLength) > ISkillPackageManagementService.MaxPackageBytes)
            throw new InvalidOperationException("Updated Skill package exceeds the 4 MB limit.");
        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.InspectFiles(merged, cancellationToken);

        (FileObjectReference stored, string storedIndexHash) = await StorePackageAsync(
            tenantId, userId, "text/markdown", merged, cancellationToken).ConfigureAwait(false);
        var updated = new SkillInstanceConfig
        {
            Id = skill.Id,
            TenantId = skill.TenantId,
            Name = skill.Name,
            Enabled = skill.Enabled,
            Description = metadata.Description,
            ParametersJsonSchema = skill.ParametersJsonSchema,
            Type = skill.Type,
            Source = skill.Source,
            SourceType = skill.SourceType,
            SourceId = skill.SourceId,
            PackageFileName = skill.PackageFileName,
            PackageFormat = "directory",
            ObjectKey = stored.ObjectKey,
            Sha256 = storedIndexHash,
            ResourceCount = metadata.ResourceCount,
            AllowedUserIds = skill.AllowedUserIds,
            AllowedGroups = skill.AllowedGroups,
            AllowedTenantIds = skill.AllowedTenantIds,
            AllowedRoles = skill.AllowedRoles
        };
        try
        {
            await skillCatalog.PublishAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeletePackageBestEffortAsync(tenantId, updated.ObjectKey, updated.PackageFormat).ConfigureAwait(false);
            throw;
        }

        await DeletePackageBestEffortAsync(tenantId, skill.ObjectKey, skill.PackageFormat).ConfigureAwait(false);
        return updated;
    }

    public async Task<bool> DeleteCatalogAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (skillCatalog == null) return false;
        SkillInstanceConfig? skill = await skillCatalog.GetAsync(
            tenantId,
            skillId,
            cancellationToken).ConfigureAwait(false);
        if (skill == null) return false;
        await DeletePackageBestEffortAsync(tenantId, skill.ObjectKey, skill.PackageFormat).ConfigureAwait(false);
        await skillCatalog.RemoveAsync(tenantId, skillId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<string?> ReadMarkdownAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (skillCatalog == null) return null;
        SkillInstanceConfig? skill = await skillCatalog.GetAsync(
            tenantId,
            skillId,
            cancellationToken).ConfigureAwait(false);
        if (skill == null || string.IsNullOrWhiteSpace(skill.ObjectKey)) return null;
        EnsureTenantSharedObjectKey(skill.ObjectKey, tenantId);

        if (string.Equals(skill.PackageFormat, "directory", StringComparison.OrdinalIgnoreCase))
        {
            byte[] indexContent = await objectStore.ReadAsync(skill.ObjectKey, cancellationToken).ConfigureAwait(false);
            SkillPackageStorageIndex index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(indexContent)
                ?? throw new InvalidOperationException($"Skill package '{skillId}' has an invalid storage index.");
            EnsureTenantIndex(index, tenantId);
            SkillPackageStorageFile? markdown = index.Files.FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file.RelativePath), "SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (markdown == null) return null;
            EnsureTenantSharedObjectKey(markdown.ObjectKey, tenantId);
            byte[] content = await objectStore.ReadAsync(markdown.ObjectKey, cancellationToken).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(content);
        }

        byte[] package = await objectStore.ReadAsync(skill.ObjectKey, cancellationToken).ConfigureAwait(false);
        SkillPackageFile? legacyMarkdown = AgentSkillPackageArchive.ReadZipFiles(package, cancellationToken)
            .FirstOrDefault(file => string.Equals(Path.GetFileName(file.RelativePath), "SKILL.md", StringComparison.OrdinalIgnoreCase));
        return legacyMarkdown == null ? null : System.Text.Encoding.UTF8.GetString(legacyMarkdown.Content);
    }

    public async Task<SkillPackageDeleteResult> DeleteAsync(
        string agentId,
        string tenantId,
        string skillId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        AgentConfigEntity? entity = await agentConfigs
            .GetAgentAsync(agentId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (entity == null)
        {
            return SkillPackageDeleteResult.AgentNotFound;
        }
        if (!string.Equals(entity.TenantId, tenantId, StringComparison.Ordinal))
        {
            return SkillPackageDeleteResult.TenantMismatch;
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
        AgentConfigEntity? saved = await agentConfigs.SaveAgentAsync(
            agentId,
            tenantId,
            entity,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        if (saved == null)
        {
            return SkillPackageDeleteResult.Conflict;
        }

        await DeletePackageBestEffortAsync(tenantId, instance.ObjectKey, instance.PackageFormat).ConfigureAwait(false);

        return SkillPackageDeleteResult.Deleted;
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

        if (buffer.Length > ISkillPackageManagementService.MaxPackageBytes)
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

    private async Task DeletePackageBestEffortAsync(string tenantId, string? objectKey, string? packageFormat)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return;
        EnsureTenantSharedObjectKey(objectKey, tenantId);

        if (string.Equals(packageFormat, "directory", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] indexContent = await objectStore.ReadAsync(objectKey, CancellationToken.None).ConfigureAwait(false);
                SkillPackageStorageIndex? index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(indexContent);
                if (index != null)
                {
                    EnsureTenantIndex(index, tenantId);
                    foreach (SkillPackageStorageFile file in index.Files)
                    {
                        EnsureTenantSharedObjectKey(file.ObjectKey, tenantId);
                        await DeleteObjectBestEffortAsync(file.ObjectKey).ConfigureAwait(false);
                    }
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
        string tenantId,
        byte[] indexContent,
        CancellationToken cancellationToken)
    {
        SkillPackageStorageIndex index = JsonSerializer.Deserialize<SkillPackageStorageIndex>(indexContent)
            ?? throw new InvalidOperationException("Skill storage index is invalid.");
        EnsureTenantIndex(index, tenantId);
        var files = new List<SkillPackageFile>(index.Files.Count);
        foreach (SkillPackageStorageFile file in index.Files)
        {
            EnsureTenantSharedObjectKey(file.ObjectKey, tenantId);
            byte[] content = await objectStore.ReadAsync(file.ObjectKey, cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Skill file '{file.RelativePath}' failed its SHA-256 integrity check.");
            files.Add(new SkillPackageFile(file.RelativePath, content));
        }
        return files;
    }

    private static void EnsureTenantIndex(SkillPackageStorageIndex index, string tenantId)
    {
        if (!string.Equals(index.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Skill storage index belongs to another tenant.");
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
