using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Runtime.OpenCode;
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

    [Theory]
    [InlineData(null, "300", 14, true)]
    [InlineData("0", "300", 0, true)]
    [InlineData("30", "60", 30, true)]
    [InlineData("-1", "300", 0, false)]
    [InlineData("4000", "300", 0, false)]
    // 保留期不长于保活窗口时，共享存储上另一实例的空闲进程可能比它的会话状态活得久。
    [InlineData("1", "86400", 0, false)]
    public void Bind_SessionStateRetentionDays_DefaultsTo14AndMustOutliveTheKeepAliveWindow(
        string? configured, string keepAlive, int expected, bool valid)
    {
        Dictionary<string, string?> settings = new() { ["OpenCode:KeepAliveSeconds"] = keepAlive };
        if (configured is not null)
        {
            settings["OpenCode:SessionStateRetentionDays"] = configured;
        }
        ServiceCollection services = new();
        services.AddOpenCodeRuntime(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        if (!valid)
        {
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OpenCodeOptions>>().Value);
            return;
        }
        Assert.Equal(expected, provider.GetRequiredService<IOptions<OpenCodeOptions>>().Value.SessionStateRetentionDays);
    }
}
