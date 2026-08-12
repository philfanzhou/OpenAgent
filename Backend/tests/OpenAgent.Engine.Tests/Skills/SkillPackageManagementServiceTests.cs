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
    private const string SkillYaml = """
        id: customer-lookup
        name: Customer lookup
        description: Looks up a customer
        endpointUrl: https://skills.example.test/customer
        version: 1.0.0
        """;

    [Fact]
    public async Task InstallAsync_WritesPackageToObjectStorageAndUpdatesAgent()
    {
        (SkillPackageManagementService service, AgentConfigManagementService configs, RecordingObjectStore store) =
            await CreateServiceAsync();

        await using var package = new MemoryStream(Encoding.UTF8.GetBytes(SkillYaml));
        SkillPackageInstallResult result = await service.InstallAsync(
            AgentId,
            "tenant",
            "customer.yaml",
            "application/yaml",
            package,
            expectedVersion: null,
            default);

        Assert.True(result.AgentExists);
        Assert.False(result.HasConflict);
        Assert.Equal("skills/customer.yaml", result.Skill?.ObjectKey);
        Assert.Equal(Encoding.UTF8.GetBytes(SkillYaml), store.Content);
        AgentConfigEntity? saved = await configs.GetAsync(AgentId);
        SkillInstanceConfig skill = Assert.Single(saved!.Config.Skills.Instances);
        Assert.Equal("customer-lookup", skill.Id);
        Assert.Contains("customer-lookup", saved.Config.Skills.EnabledSkills);
    }

    [Fact]
    public async Task ValidateAsync_ReadsPackageFromObjectStorageAndVerifiesHash()
    {
        (SkillPackageManagementService service, _, RecordingObjectStore store) = await CreateServiceAsync();
        byte[] content = Encoding.UTF8.GetBytes(SkillYaml);
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
                    PackageFileName = "customer.yaml",
                    ObjectKey = "skills/customer.yaml",
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()
                }
            ]
        };

        SkillPackageValidationResult result = await service.ValidateAsync(skills, default);

        Assert.True(result.Success);
        Assert.Equal(["customer-lookup"], result.ObjectStorageVerifiedSkills);
        Assert.Equal("skills/customer.yaml", store.LastReadObjectKey);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAgentBindingAndStoredObject()
    {
        (SkillPackageManagementService service, AgentConfigManagementService configs, RecordingObjectStore store) =
            await CreateServiceAsync();
        await using var package = new MemoryStream(Encoding.UTF8.GetBytes(SkillYaml));
        await service.InstallAsync(
            AgentId,
            "tenant",
            "customer.yaml",
            "application/yaml",
            package,
            expectedVersion: null,
            default);

        SkillPackageDeleteResult result = await service.DeleteAsync(
            AgentId,
            "customer-lookup",
            expectedVersion: null,
            default);

        Assert.Equal(SkillPackageDeleteResult.Deleted, result);
        Assert.Equal("skills/customer.yaml", store.DeletedObjectKey);
        AgentConfigEntity? saved = await configs.GetAsync(AgentId);
        Assert.Empty(saved!.Config.Skills.Instances);
        Assert.Empty(saved.Config.Skills.EnabledSkills);
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
        return (new SkillPackageManagementService(configs, store, new TestSkillPackageReader()), configs, store);
    }

    private sealed class TestSkillPackageReader : OpenAgent.Contracts.Skills.ISkillPackageReader
    {
        public OpenAgent.Contracts.Skills.SkillPackageManifest Read(
            string fileName,
            ReadOnlyMemory<byte> content) => new()
            {
                Id = "customer-lookup",
                Name = "Customer lookup",
                Description = "Looks up a customer",
                EndpointUrl = "https://skills.example.test/customer",
                Version = "1.0.0"
            };

        public string GetFormat(string fileName) => "yaml";
    }

    private sealed class RecordingObjectStore : IFileObjectStore
    {
        public byte[] Content { get; set; } = [];
        public string? LastReadObjectKey { get; private set; }
        public string? DeletedObjectKey { get; private set; }

        public async Task<FileObjectReference> WriteAsync(
            FileObjectWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Content = buffer.ToArray();
            return new FileObjectReference { ObjectKey = $"skills/{request.FileName}" };
        }

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            LastReadObjectKey = objectKey;
            return Task.FromResult(Content);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeletedObjectKey = objectKey;
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
