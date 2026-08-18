using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Skills;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Reload;
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
        (SkillPackageManagementService service, AgentConfigManagementService configs, RecordingObjectStore store) =
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
        AgentConfigEntity? saved = await configs.GetAsync(AgentId);
        SkillInstanceConfig skill = Assert.Single(saved!.Config.Skills.Instances);
        Assert.Equal("customer-lookup", skill.Id);
        Assert.Contains("customer-lookup", saved.Config.Skills.EnabledSkills);
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

        SkillPackageValidationResult result = await service.ValidateAsync(skills, default);

        Assert.True(result.Success);
        Assert.Equal(["customer-lookup"], result.ObjectStorageVerifiedSkills);
        Assert.NotNull(store.LastReadObjectKey);
        Assert.Contains(store.LastReadObjectKey!, store.Objects.Keys);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAgentBindingAndStoredObject()
    {
        (SkillPackageManagementService service, AgentConfigManagementService configs, RecordingObjectStore store) =
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
            "customer-lookup",
            expectedVersion: null,
            default);

        Assert.Equal(SkillPackageDeleteResult.Deleted, result);
        Assert.True(store.DeletedObjectKeys.Count >= 2);
        AgentConfigEntity? saved = await configs.GetAsync(AgentId);
        Assert.Empty(saved!.Config.Skills.Instances);
        Assert.Empty(saved.Config.Skills.EnabledSkills);
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
    public async Task UploadAsync_RecordsContentAndRequiresSeparateScriptAuthorization()
    {
        (SkillPackageManagementService service, _, _) = await CreateServiceAsync();
        byte[] content = CreatePackage(archive =>
        {
            WriteEntry(archive, "customer-lookup/resources/sample.csv", "id\n42\n");
            WriteEntry(archive, "customer-lookup/scripts/lookup.py", "print(42)\n");
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
        Assert.Equal(1, result.Skill.ScriptCount);
        Assert.False(result.Skill.AllowScriptExecution);
    }

    private static async Task<(
        SkillPackageManagementService Service,
        AgentConfigManagementService Configs,
        RecordingObjectStore Store)> CreateServiceAsync()
    {
        var environment = new Mock<IHostEnvironment>();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:AllowMockAgent"] = "true"
            })
            .Build();
        var snapshot = new ConfigSnapshot(
            Options.Create(new ConfigSnapshotOptions()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ConfigSnapshot>.Instance);
        var redis = new UnavailableRedisConnectionProvider();
        var configs = new AgentConfigManagementService(
            redis,
            new MockAgentResolver(environment.Object, configuration),
            new AgentConfigLocalStore(),
            CreateDispatcher(redis, snapshot));
        await configs.SaveAsync(AgentId, new AgentConfigEntity { AgentId = AgentId }, expectedVersion: null);
        var store = new RecordingObjectStore();
        return (new SkillPackageManagementService(
            configs,
            store,
            NullLogger<SkillPackageManagementService>.Instance), configs, store);
    }

    private static ConfigUpdateDispatcher CreateDispatcher(
        IRedisConnectionProvider redis,
        ConfigSnapshot snapshot)
    {
        var llmRegistry = new Mock<ILlmRegistry>();
        var fullConfig = new FullConfigRefresher(
            redis,
            snapshot,
            NullLogger<FullConfigRefresher>.Instance);
        var llmProfiles = new LlmProfileRefresher(
            redis,
            llmRegistry.Object,
            NullLogger<LlmProfileRefresher>.Instance);
        return new ConfigUpdateDispatcher(
            fullConfig,
            llmProfiles,
            new LegacyMessageHandler(
                fullConfig,
                llmProfiles,
                NullLogger<LegacyMessageHandler>.Instance),
            snapshot,
            NullLogger<ConfigUpdateDispatcher>.Instance);
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

    private sealed class RecordingObjectStore : IFileObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
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
            string objectKey = $"skills/{request.FileId}{Path.GetExtension(request.FileName)}";
            Objects[objectKey] = buffer.ToArray();
            WriteCount++;
            return new FileObjectReference { ObjectKey = objectKey };
        }

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            LastReadObjectKey = objectKey;
            return Task.FromResult(Objects[objectKey]);
        }

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
}
