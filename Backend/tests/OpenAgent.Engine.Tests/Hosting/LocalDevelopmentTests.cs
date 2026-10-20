using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Extensions;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class LocalDevelopmentTests
{
    [Theory]
    [InlineData(FileObjectScope.Tenant)]
    [InlineData(FileObjectScope.User)]
    public async Task ObjectStore_WriteRetainsTenantPartitionAndDoesNotOverwriteOtherTenants(FileObjectScope scope)
    {
        LocalInMemoryFileObjectStore store = new();
        async Task<FileObjectReference> Write(string tenant, string text) => await store.WriteAsync(new()
        {
            FileId = "same-id", TenantId = tenant, UserId = "user", Scope = scope,
            FileName = "SKILL.md", MediaType = "text/markdown", Sha256 = "test"
        }, new MemoryStream(Encoding.UTF8.GetBytes(text)), default);
        FileObjectReference first = await Write("tenant-a", "first");
        FileObjectReference second = await Write("tenant-b", "second");
        Assert.True(FileObjectTenantScope.ContainsTenantPartition(first.ObjectKey, "tenant-a"));
        Assert.False(FileObjectTenantScope.ContainsTenantPartition(first.ObjectKey, "tenant-b"));
        Assert.Equal(scope == FileObjectScope.Tenant, FileObjectTenantScope.ContainsTenantSharedPartition(first.ObjectKey, "tenant-a"));
        Assert.Equal("first", Encoding.UTF8.GetString(await store.ReadAsync(first.ObjectKey, default)));
        Assert.Equal("second", Encoding.UTF8.GetString(await store.ReadAsync(second.ObjectKey, default)));
    }

    [Fact]
    public async Task ObjectStore_BoundedReadRejectsOversizeContent()
    {
        LocalInMemoryFileObjectStore store = new();
        FileObjectReference file = await store.WriteAsync(new()
        {
            FileId = "large", TenantId = "tenant", UserId = "user", FileName = "file.txt",
            MediaType = "text/plain", Sha256 = "test"
        }, new MemoryStream([1, 2, 3]), default);
        await Assert.ThrowsAsync<AgentException>(() => store.ReadAsync(file.ObjectKey, 2, default));
    }

    [Fact]
    public void CurrentUser_ChangesWithTheRequestInsteadOfCapturingFirstUser()
    {
        HttpContextAccessor accessor = new();
        LocalDevelopmentCurrentUserContext user = new(accessor);
        foreach (string id in new[] { "admin", "test" })
        {
            accessor.HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id)], "test"))
            };
            Assert.Equal(id, user.UserId);
        }
        accessor.HttpContext = null;
        Assert.Equal("anonymous", user.UserId);
    }

    [Fact]
    public async Task ModelList_RedactingResponseDoesNotEraseStoredCredential()
    {
        InMemoryLlmConfigRepository repository = new();
        await repository.UpsertAsync("tenant", "model", new LlmProviderProfile
        {
            Id = "model", TenantId = "tenant", ApiKey = "test-secret"
        });
        LlmProviderProfile listed = Assert.Single(await repository.ListAsync("tenant"));
        listed.ApiKey = string.Empty;
        Assert.Equal("test-secret", (await repository.GetAsync("tenant", "model"))!.ApiKey);
    }

    [Fact]
    public async Task SkillCatalog_PublishedSkillsAreVisibleToExecutionWithoutRedis()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddLocalInMemoryStores();
        services.AddAgentEngine(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        ISkillCatalogStore writer = provider.GetRequiredService<ISkillCatalogStore>();
        ISkillCatalog reader = provider.GetRequiredService<ISkillCatalog>();
        await writer.PublishAsync(new SkillInstanceConfig { TenantId = "tenant-a", Id = "test-skill", Name = "test-skill", Type = SkillTypes.AgentSkill });
        Assert.NotNull(await reader.GetAsync("tenant-a", "test-skill"));
        Assert.Null(await reader.GetAsync("tenant-b", "test-skill"));
    }
}
