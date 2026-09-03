using Microsoft.Extensions.Configuration;
using OpenAgent.Engine.Config;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public sealed class ConfigurationSecretResolverTests
{
    [Fact]
    public async Task ProtectAndResolveAsync_RoundTripsTenantSecret()
    {
        var resolver = new ConfigurationSecretResolver(new ConfigurationBuilder().Build());

        string protectedSecret = await resolver.ProtectAsync("tenant-a", "tenant-a-key");

        Assert.StartsWith("v1.", protectedSecret, StringComparison.Ordinal);
        Assert.Equal("tenant-a-key", await resolver.ResolveAsync("tenant-a", protectedSecret));
        Assert.Null(await resolver.ResolveAsync("tenant-b", protectedSecret));
    }

    [Fact]
    public async Task ResolveAsync_UsesTenantScopedConfigurationPath()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:tenant-a:llm:production"] = "tenant-a-key",
                ["Secrets:tenant-b:llm:production"] = "tenant-b-key"
            })
            .Build();
        var resolver = new ConfigurationSecretResolver(configuration);

        string? tenantA = await resolver.ResolveAsync("tenant-a", "llm:production");
        string? tenantB = await resolver.ResolveAsync("tenant-b", "llm:production");

        Assert.Equal("tenant-a-key", tenantA);
        Assert.Equal("tenant-b-key", tenantB);
    }

    [Theory]
    [InlineData("tenant/a", "llm:production")]
    public async Task ResolveAsync_InvalidPath_Throws(
        string tenantId,
        string secretReference)
    {
        var resolver = new ConfigurationSecretResolver(new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(tenantId, secretReference));
    }
}
