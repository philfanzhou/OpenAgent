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
}
