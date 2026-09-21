using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Exten;
using OpenAgent.Infrastructure;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public sealed class ConversationStoreRegistrationTests
{
    [Fact]
    public void AgentCoreWithInfrastructure_NarrowContracts_ResolveToSameScopedInstance()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "PostgreSql",
                ["ConnectionStrings:OpenAgentDatabase"] = "Host=localhost;Database=openagent;Username=agent;Password=agent"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<Contracts.Security.ICurrentUserContext, FakeUserContext>();

        services.AddAgentCore(configuration);
        services.AddOpenAgentInfrastructure(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IConversationStore store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        IConversationReader reader = scope.ServiceProvider.GetRequiredService<IConversationReader>();
        IConversationWriter writer = scope.ServiceProvider.GetRequiredService<IConversationWriter>();

        Assert.Same(store, reader);
        Assert.Same(store, writer);
    }

    private sealed class FakeUserContext : Contracts.Security.ICurrentUserContext
    {
        public string UserId => "user-1";
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }
}
