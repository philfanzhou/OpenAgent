using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class AgentProviderRegistryTests
{
    [Fact]
    public void Constructor_ProviderSpecificSettings_ArePassedToFactory()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RouterSettings:AgentProviders:DefaultProviderId"] = "partner",
                ["RouterSettings:AgentProviders:Providers:0:Id"] = "partner",
                ["RouterSettings:AgentProviders:Providers:0:Type"] = "Partner",
                ["RouterSettings:AgentProviders:Providers:0:Settings:ApiKey"] = "secret",
                ["RouterSettings:AgentProviders:Providers:0:Settings:Custom:Region"] = "east"
            })
            .Build();
        var factory = new RecordingFactory();
        AgentProviderOptions options = new()
        {
            DefaultProviderId = "partner",
            Providers =
            [
                new AgentProviderDefinition { Id = "partner", Type = "Partner" }
            ]
        };

        using var registry = new AgentProviderRegistry(
            [factory],
            Microsoft.Extensions.Options.Options.Create(options),
            configuration);

        Assert.Equal("partner", registry.DefaultProvider.Id);
        Assert.Equal("secret", factory.ApiKey);
        Assert.Equal("east", factory.Region);
    }

    private sealed class RecordingFactory : IAgentProviderFactory
    {
        public string Type => "Partner";
        public string? ApiKey { get; private set; }
        public string? Region { get; private set; }

        public IAgentProvider Create(string providerId, IConfigurationSection settings)
        {
            ApiKey = settings["ApiKey"];
            Region = settings["Custom:Region"];
            return new StubProvider(providerId);
        }
    }

    private sealed class StubProvider(string id) : IAgentProvider
    {
        public string Id => id;

        public Task<AgentProviderCatalog> GetAgentsAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentProviderCatalog([]));

        public Task<AgentProviderConversation> ResolveConversationAsync(
            AgentProviderRequestContext requestContext,
            string conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentProviderConversation(
                AgentProviderConversationStatus.NotFound));

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
            string intentAgentId,
            IReadOnlyList<AgentSummary> agents,
            string message,
            CancellationToken cancellationToken) =>
            Task.FromResult<IntentRecognitionResult?>(null);

        public Task<AgentForwardingTarget?> ResolveForwardingAsync(
            string? action,
            string? tenantId,
            string? conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AgentForwardingTarget?>(null);

        public ValueTask ConfigureRequestAsync(
            HttpRequestMessage request,
            AgentForwardingTarget target,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
