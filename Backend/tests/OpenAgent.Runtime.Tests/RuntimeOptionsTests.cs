using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class RuntimeOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bind_DefaultOrConfiguredArguments_StartsAcpExactlyOnce(bool configured)
    {
        Dictionary<string, string?> settings = [];
        if (configured)
        {
            settings["OpenCode:Arguments:0"] = "acp";
        }
        ServiceCollection services = new();
        services.AddOpenCodeRuntime(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal("acp", Assert.Single(provider.GetRequiredService<IOptions<OpenCodeOptions>>().Value.Arguments));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Bind_LogUsageUpdates_DefaultsOffAndFollowsConfig(string? configured, bool expected)
    {
        Dictionary<string, string?> settings = [];
        if (configured is not null)
        {
            settings["OpenCode:LogUsageUpdates"] = configured;
        }
        ServiceCollection services = new();
        services.AddOpenCodeRuntime(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<IOptions<OpenCodeOptions>>().Value.LogUsageUpdates);
    }
}
