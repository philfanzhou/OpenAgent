using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Skills;
using OpenAgent.Engine.Models;
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
        Assert.Equal("skills/customer.zip", result.Skill?.ObjectKey);
        Assert.Equal(content, store.Content);
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
        store.Content = content;
        var skills = new SkillsConfig
        {
            EnabledSkills = ["customer-lookup"],
            Instances =
            [
                new SkillInstanceConfig
                {
                    Id = "customer-lookup",
                    Name = "Customer lookup",
                    Enabled = true,
                    PackageFileName = "customer.zip",
                    ObjectKey = "skills/customer.zip",
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()
                }
            ]
        };

        SkillPackageValidationResult result = await service.ValidateAsync(skills, default);

        Assert.True(result.Success);
        Assert.Equal(["customer-lookup"], result.ObjectStorageVerifiedSkills);
        Assert.Equal("skills/customer.zip", store.LastReadObjectKey);
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
        Assert.Equal("skills/customer.zip", store.DeletedObjectKey);
        AgentConfigEntity? saved = await configs.GetAsync(AgentId);
        Assert.Empty(saved!.Config.Skills.Instances);
        Assert.Empty(saved.Config.Skills.EnabledSkills);
    }

    [Fact]
    public async Task InstallAsync_ReplacingPackage_DeletesPreviousObject()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();
        byte[] content = CreatePackage();

        await service.InstallAsync(
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

        Assert.Equal("skills/customer.zip-2", result.Skill?.ObjectKey);
        Assert.Contains("skills/customer.zip", store.DeletedObjectKeys);
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
        var configs = new AgentConfigManagementService(
            new UnavailableRedisConnectionProvider(),
            new MockAgentResolver(environment.Object, configuration),
            new AgentConfigLocalStore(),
            snapshot);
        await configs.SaveAsync(AgentId, new AgentConfigEntity { AgentId = AgentId }, expectedVersion: null);
        var store = new RecordingObjectStore();
        return (new SkillPackageManagementService(
            configs,
            store,
            NullLogger<SkillPackageManagementService>.Instance), configs, store);
    }

    private static byte[] CreatePackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (StreamWriter writer = new(archive.CreateEntry("customer-lookup/SKILL.md").Open(), Encoding.UTF8))
        {
            writer.Write(SkillMarkdown);
        }
        return stream.ToArray();
    }

    private sealed class RecordingObjectStore : IFileObjectStore
    {
        public byte[] Content { get; set; } = [];
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
            Content = buffer.ToArray();
            WriteCount++;
            string objectKey = WriteCount == 1
                ? $"skills/{request.FileName}"
                : $"skills/{request.FileName}-{WriteCount}";
            return new FileObjectReference { ObjectKey = objectKey };
        }

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            LastReadObjectKey = objectKey;
            return Task.FromResult(Content);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeletedObjectKeys.Add(objectKey);
            return Task.CompletedTask;
        }
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
