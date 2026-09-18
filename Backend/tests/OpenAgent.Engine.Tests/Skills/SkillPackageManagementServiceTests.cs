using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Skills;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Skills;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Skills;

public class SkillPackageManagementServiceTests
{
    private const string AgentId = "support";
    private const string SkillMarkdown = """
        ---
        name: customer-lookup
        description: Looks up customers
        ---

        # Instructions

        Use the customer system.
        """;

    [Fact]
    public async Task InstallAsync_WritesPackageToObjectStorageAndUpdatesAgent()
    {
        (SkillPackageManagementService service, ConfigurationService configs, RecordingObjectStore store) =
            await CreateServiceAsync();

        byte[] content = CreatePackage();
        await using var package = new MemoryStream(content);
        SkillPackageInstallResult result = await service.InstallAsync(
            AgentId,
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            package,
            expectedVersion: null,
            default);

        Assert.True(result.AgentExists);
        Assert.False(result.HasConflict);
        Assert.Equal("directory", result.Skill?.PackageFormat);
        Assert.NotNull(result.Skill?.ObjectKey);
        SkillPackageStorageIndex storage = store.ReadIndex(result.Skill!.ObjectKey!);
        Assert.Equal(content.Length > 0, storage.Files.Count == 1);
        Assert.Equal(SkillMarkdown, Encoding.UTF8.GetString(store.Objects[storage.Files[0].ObjectKey]).TrimStart('\uFEFF'));
        AgentConfigEntity? saved = await configs.GetAgentAsync(AgentId, "tenant");
        SkillInstanceConfig skill = Assert.Single(saved!.Config.Skills.Instances);
        Assert.Equal("customer-lookup", skill.Id);
        Assert.Equal("tenant", skill.TenantId);
        Assert.Equal(SkillTypes.AgentSkill, skill.Type);
        Assert.Equal(SkillSourceTypes.ObjectStorage, skill.SourceType);
        Assert.Contains("customer-lookup", saved.Config.Skills.EnabledSkills);
        Assert.True(FileObjectTenantScope.ContainsTenantSharedPartition(skill.ObjectKey!, "tenant"));
        Assert.DoesNotContain("/users/", skill.ObjectKey!, StringComparison.Ordinal);
        Assert.All(store.WriteRequests, request => Assert.Equal(FileObjectScope.Tenant, request.Scope));
    }

    [Fact]
    public async Task ValidateAsync_ReadsPackageFromObjectStorageAndVerifiesHash()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();
        byte[] content = CreatePackage();
        await using var package = new MemoryStream(content);
        SkillPackageInstallResult installed = await service.InstallAsync(
            AgentId, "tenant", "user", "customer.zip", "application/zip", package, null, default);
        SkillInstanceConfig storedSkill = installed.Skill!;
        var skills = new SkillsConfig
        {
            EnabledSkills = ["customer-lookup"],
            Instances =
            [
                storedSkill
            ]
        };

        SkillPackageValidationResult result = await service.ValidateAsync("tenant", skills, default);

        Assert.True(result.Success);
        Assert.Equal(["customer-lookup"], result.ObjectStorageVerifiedSkills);
        Assert.NotNull(store.LastReadObjectKey);
        Assert.Contains(store.LastReadObjectKey!, store.Objects.Keys);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAgentBindingAndStoredObject()
    {
        (SkillPackageManagementService service, ConfigurationService configs, RecordingObjectStore store) =
            await CreateServiceAsync();
        await using var package = new MemoryStream(CreatePackage());
        await service.InstallAsync(
            AgentId,
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            package,
            expectedVersion: null,
            default);

        SkillPackageDeleteResult result = await service.DeleteAsync(
            AgentId,
            "tenant",
            "customer-lookup",
            expectedVersion: null,
            default);

        Assert.Equal(SkillPackageDeleteResult.Deleted, result);
        Assert.True(store.DeletedObjectKeys.Count >= 2);
        AgentConfigEntity? saved = await configs.GetAgentAsync(AgentId, "tenant");
        Assert.Empty(saved!.Config.Skills.Instances);
        Assert.Empty(saved.Config.Skills.EnabledSkills);
    }

    [Fact]
    public async Task InstallAsync_AgentOwnedByAnotherTenant_ReturnsNotFound()
    {
        (SkillPackageManagementService service, ConfigurationService configs, RecordingObjectStore store) =
            await CreateServiceAsync("tenant-a");

        SkillPackageInstallResult result = await service.InstallAsync(
            AgentId,
            "tenant-b",
            "user",
            "customer.md",
            "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes(SkillMarkdown)),
            expectedVersion: null,
            default);

        Assert.False(result.AgentExists);
        Assert.Empty(store.Objects);
        Assert.Equal("tenant-a", (await configs.GetAgentAsync(AgentId, "tenant-a"))!.TenantId);
    }

    [Fact]
    public async Task ValidateAsync_SkillOwnedByAnotherTenant_DoesNotReadObjectStorage()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();
        var skills = new SkillsConfig
        {
            Instances =
            [
                new SkillInstanceConfig
                {
                    Id = "customer-lookup",
                    Name = "customer-lookup",
                    TenantId = "tenant-b",
                    ObjectKey = "private/tenants/foreign/users/foreign/skill.json"
                }
            ]
        };

        SkillPackageValidationResult result = await service.ValidateAsync("tenant-a", skills, default);

        Assert.False(result.Success);
        Assert.Null(store.LastReadObjectKey);
    }

    [Fact]
    public async Task InstallAsync_ReplacingPackage_DeletesPreviousObject()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();
        byte[] content = CreatePackage();

        SkillPackageInstallResult first = await service.InstallAsync(
            AgentId,
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            new MemoryStream(content),
            expectedVersion: null,
            default);
        SkillPackageInstallResult result = await service.InstallAsync(
            AgentId,
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            new MemoryStream(content),
            expectedVersion: null,
            default);

        Assert.NotEqual(first.Skill?.ObjectKey, result.Skill?.ObjectKey);
        Assert.Contains(first.Skill!.ObjectKey!, store.DeletedObjectKeys);
    }

    [Fact]
    public async Task InstallAsync_AcceptsSingleMarkdownSkill()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();

        SkillPackageInstallResult result = await service.InstallAsync(
            AgentId,
            "tenant",
            "user",
            "customer.md",
            "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes(SkillMarkdown)),
            expectedVersion: null,
            default);

        Assert.Equal("customer-lookup", result.Skill?.Id);
        Assert.Equal("directory", result.Skill?.PackageFormat);
        Assert.Equal(2, store.Objects.Count);
    }

    [Fact]
    public async Task UploadAsync_RecordsResourceCount()
    {
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync();
        byte[] content = CreatePackage(archive =>
        {
            WriteEntry(archive, "customer-lookup/resources/sample.csv", "id\n42\n");
        });

        SkillPackageUploadResult result = await service.UploadAsync(
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            new MemoryStream(content),
            default,
            publishCatalog: false);

        Assert.Equal(1, result.Skill.ResourceCount);
    }

    [Fact]
    public async Task UploadAsync_RecordsPythonScriptInventory()
    {
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync();
        byte[] content = CreatePackage(archive =>
        {
            WriteEntry(archive, "customer-lookup/scripts/lookup.py", "print('lookup')");
            WriteEntry(archive, "customer-lookup/notes.txt", "plain text");
        });

        SkillPackageUploadResult result = await service.UploadAsync(
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            new MemoryStream(content),
            default,
            publishCatalog: false);

        Assert.Equal(["customer-lookup/scripts/lookup.py"], result.Skill.ScriptNames);
        Assert.Equal(1, result.Skill.ScriptCount);
        Assert.False(result.Skill.ScriptExecutionEnabled);
    }

    [Fact]
    public async Task UploadAsync_EnableScriptExecutionWithoutScripts_Throws()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(
            "tenant",
            "user",
            "customer.md",
            "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes(SkillMarkdown)),
            default,
            publishCatalog: false,
            scriptExecutionEnabled: true));

        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task UpdateScriptExecutionAsync_RereadsInventoryFromStorageAndPublishes()
    {
        var catalog = new RecordingSkillCatalogStore();
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync(catalog: catalog);
        byte[] content = CreatePackage(archive =>
            WriteEntry(archive, "customer-lookup/scripts/lookup.py", "print('lookup')"));
        await service.UploadAsync(
            "tenant",
            "user",
            "customer.zip",
            "application/zip",
            new MemoryStream(content),
            default);

        SkillInstanceConfig? updated = await service.UpdateScriptExecutionAsync(
            "tenant",
            "customer-lookup",
            scriptExecutionEnabled: true,
            default);

        Assert.NotNull(updated);
        Assert.True(updated.ScriptExecutionEnabled);
        Assert.Equal(["customer-lookup/scripts/lookup.py"], updated.ScriptNames);
        Assert.True(catalog.Published.Last().ScriptExecutionEnabled);
    }

    [Fact]
    public async Task UpdateScriptExecutionAsync_EnablingWithoutScripts_Throws()
    {
        var catalog = new RecordingSkillCatalogStore();
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync(catalog: catalog);
        await service.UploadAsync(
            "tenant",
            "user",
            "customer.md",
            "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes(SkillMarkdown)),
            default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateScriptExecutionAsync("tenant", "customer-lookup", scriptExecutionEnabled: true, default));
        SkillInstanceConfig stored = catalog.Published.Last();
        Assert.False(stored.ScriptExecutionEnabled);

        SkillInstanceConfig? disabled = await service.UpdateScriptExecutionAsync(
            "tenant",
            "customer-lookup",
            scriptExecutionEnabled: false,
            default);
        Assert.NotNull(disabled);
        Assert.False(disabled.ScriptExecutionEnabled);
    }

    [Fact]
    public async Task UpdateScriptExecutionAsync_UnknownSkill_ReturnsNull()
    {
        var catalog = new RecordingSkillCatalogStore();
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync(catalog: catalog);

        SkillInstanceConfig? result = await service.UpdateScriptExecutionAsync(
            "tenant",
            "missing-skill",
            scriptExecutionEnabled: true,
            default);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateMarkdownAsync_ReplacesMarkdownPreservingScriptsAndFlag()
    {
        var catalog = new RecordingSkillCatalogStore();
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync(catalog: catalog);
        byte[] content = CreatePackage(archive =>
        {
            WriteEntry(archive, "customer-lookup/scripts/lookup.py", "print('lookup')");
            WriteEntry(archive, "customer-lookup/scripts/report.js", "console.log('report')");
        });
        await service.UploadAsync(
            "tenant", "user", "customer.zip", "application/zip", new MemoryStream(content), default);
        await service.UpdateScriptExecutionAsync("tenant", "customer-lookup", scriptExecutionEnabled: true, default);
        string oldObjectKey = catalog.Published.Last().ObjectKey!;

        string updatedMarkdown = """
            ---
            name: customer-lookup
            description: Refreshed description
            ---

            # Instructions

            Updated instructions.
            """;
        SkillInstanceConfig? updated = await service.UpdateMarkdownAsync(
            "tenant", "user", "customer-lookup", updatedMarkdown, default);

        Assert.NotNull(updated);
        Assert.Equal("Refreshed description", updated.Description);
        Assert.True(updated.ScriptExecutionEnabled);
        Assert.Equal(
            ["customer-lookup/scripts/lookup.py", "customer-lookup/scripts/report.js"],
            updated.ScriptNames);
        Assert.Equal(2, updated.ScriptCount);
        Assert.NotEqual(oldObjectKey, updated.ObjectKey);
        Assert.Contains("Refreshed description", await service.ReadMarkdownAsync("tenant", "customer-lookup", default));
        Assert.Equal(updated, catalog.Published.Last());
        // 旧包对象（文件 + 索引）被清理：2 个文件 + 1 个索引 = 至少 3 个删除记录
        Assert.True(store.DeletedObjectKeys.Count >= 3);
    }

    [Fact]
    public async Task UpdateMarkdownAsync_RejectsNameChange()
    {
        var catalog = new RecordingSkillCatalogStore();
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync(catalog: catalog);
        await service.UploadAsync(
            "tenant", "user", "customer.md", "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes(SkillMarkdown)), default);

        string renamed = SkillMarkdown.Replace("name: customer-lookup", "name: renamed-skill");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateMarkdownAsync("tenant", "user", "customer-lookup", renamed, default));
    }

    [Fact]
    public async Task UpdateMarkdownAsync_UnknownSkill_ReturnsNull()
    {
        var catalog = new RecordingSkillCatalogStore();
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync(catalog: catalog);

        SkillInstanceConfig? result = await service.UpdateMarkdownAsync(
            "tenant", "user", "missing-skill", SkillMarkdown, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task UploadAsync_RecordsJavaScriptScriptInventory()
    {
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync();
        byte[] content = CreatePackage(archive =>
        {
            WriteEntry(archive, "customer-lookup/scripts/lookup.py", "print('lookup')");
            WriteEntry(archive, "customer-lookup/scripts/report.js", "console.log('report')");
            WriteEntry(archive, "customer-lookup/scripts/helper.mjs", "export const x = 1;");
        });

        SkillPackageUploadResult result = await service.UploadAsync(
            "tenant", "user", "customer.zip", "application/zip",
            new MemoryStream(content), default, publishCatalog: false);

        Assert.Equal(
            ["customer-lookup/scripts/helper.mjs", "customer-lookup/scripts/lookup.py", "customer-lookup/scripts/report.js"],
            result.Skill.ScriptNames);
        Assert.Equal(3, result.Skill.ScriptCount);
    }

    private static async Task<(
        SkillPackageManagementService Service,
        ConfigurationService Configs,
        RecordingObjectStore Store)> CreateServiceAsync(
        string tenantId = "tenant",
        RecordingSkillCatalogStore? catalog = null)
    {
        var redis = new UnavailableRedisConnectionProvider();
        var configs = new ConfigurationService(
            new InMemoryAgentConfigRepository(),
            new Moq.Mock<ILlmConfigRepository>().Object,
            redis,
            Options.Create(new AgentConfigSourceOptions()),
            new ConfigurationSecretResolver(new ConfigurationBuilder().Build()),
            NullLogger<ConfigurationService>.Instance);
        await configs.SaveAgentAsync(
            AgentId,
            tenantId,
            new AgentConfigEntity { AgentId = AgentId, TenantId = tenantId },
            expectedVersion: null);
        var store = new RecordingObjectStore();
        return (new SkillPackageManagementService(
            configs,
            store,
            NullLogger<SkillPackageManagementService>.Instance,
            catalog), configs, store);
    }

    private static byte[] CreatePackage(Action<ZipArchive>? addEntries = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "customer-lookup/SKILL.md", SkillMarkdown);
            addEntries?.Invoke(archive);
        }
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class RecordingSkillCatalogStore : ISkillCatalogStore
    {
        private Dictionary<string, SkillInstanceConfig> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<SkillInstanceConfig> Published { get; } = [];

        public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillInstanceConfig>>(
                Skills.Values.Where(skill => skill.TenantId == tenantId).ToList());

        public Task<SkillInstanceConfig?> GetAsync(
            string tenantId,
            string skillId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Skills.GetValueOrDefault(Key(tenantId, skillId)));

        public Task PublishAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default)
        {
            Skills[Key(skill.TenantId, skill.Id)] = skill;
            Published.Add(skill);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string tenantId,
            string skillId,
            CancellationToken cancellationToken = default)
        {
            Skills.Remove(Key(tenantId, skillId));
            return Task.CompletedTask;
        }

        private static string Key(string tenantId, string skillId) => $"{tenantId}/{skillId}";
    }

    private sealed class RecordingObjectStore : IFileObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<FileObjectWriteRequest> WriteRequests { get; } = [];
        public string? LastReadObjectKey { get; private set; }
        public List<string> DeletedObjectKeys { get; } = [];
        public string? DeletedObjectKey => DeletedObjectKeys.LastOrDefault();
        private int WriteCount { get; set; }

        public async Task<FileObjectReference> WriteAsync(
            FileObjectWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            WriteRequests.Add(request);
            string objectKey = $"private/tenants/{FileObjectTenantScope.CreatePartition(request.TenantId)}";
            if (request.Scope == FileObjectScope.User)
            {
                objectKey += $"/users/{FileObjectTenantScope.CreatePartition(request.UserId)}";
            }

            objectKey += $"/skills/{request.FileId}{Path.GetExtension(request.FileName)}";
            Objects[objectKey] = buffer.ToArray();
            WriteCount++;
            return new FileObjectReference { ObjectKey = objectKey };
        }

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            LastReadObjectKey = objectKey;
            return Task.FromResult(Objects[objectKey]);
        }

        public Task<byte[]> ReadAsync(
            string objectKey,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            LastReadObjectKey = objectKey;
            byte[] content = Objects[objectKey];
            if (content.LongLength > maxBytes)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    $"File object '{objectKey}' exceeds the configured {maxBytes} byte limit.");
            }

            return Task.FromResult(content);
        }

        public Task<FileObjectAccessReference> CreateReadUrlAsync(
            string objectKey,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FileObjectAccessReference
            {
                ObjectKey = objectKey,
                Url = $"https://storage.example/{Uri.EscapeDataString(objectKey)}",
                ExpiresAt = expiresAt
            });

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeletedObjectKeys.Add(objectKey);
            Objects.Remove(objectKey);
            return Task.CompletedTask;
        }

        public SkillPackageStorageIndex ReadIndex(string objectKey) =>
            JsonSerializer.Deserialize<SkillPackageStorageIndex>(Objects[objectKey])!;
    }

    private sealed class UnavailableRedisConnectionProvider : IRedisConnectionProvider
    {
        public bool IsAvailable => false;
        public IServer? GetServer(int database = 0) => null;
        public IDatabase GetDatabase(int database = 0) => throw new NotSupportedException();
        public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
            Task.FromResult(RedisValue.Null);
        public Task<bool> StringSetAsync(
            RedisKey key,
            RedisValue value,
            TimeSpan? expiry = null,
            CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
        public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
            Task.FromResult(Array.Empty<RedisValue>());
        public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None) => RedisValue.Null;
        public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryAgentConfigRepository : IAgentConfigRepository
    {
        private readonly Dictionary<(string TenantId, string AgentId), AgentConfigEntity> _entities = [];

        public Task<AgentConfigEntity?> GetAsync(
            string tenantId,
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_entities.GetValueOrDefault((tenantId, agentId)));

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>(_entities.Values
                .Where(entity => tenantId == null || entity.TenantId == tenantId)
                .ToArray());

        public Task<AgentConfigEntity?> UpsertAsync(
            string tenantId,
            string agentId,
            AgentConfigEntity entity,
            string? expectedVersion,
            CancellationToken cancellationToken = default)
        {
            entity.TenantId = tenantId;
            entity.AgentId = agentId;
            entity.CurrentVersion = (long.TryParse(entity.CurrentVersion, out long version)
                    ? version + 1
                    : 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            _entities[(tenantId, agentId)] = entity;
            return Task.FromResult<AgentConfigEntity?>(entity);
        }
    }
}
