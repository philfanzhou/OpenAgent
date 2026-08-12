using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Models;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class McpCapabilitySourceTests
{
    [Fact]
    public async Task DiscoverAsync_UnavailableServer_IsNotConnectedOrExposed()
    {
        var client = new FakeMcpClient();
        var source = Source(client, (_, _) => false);

        var capabilities = await source.DiscoverAsync(
            "agent",
            Config(),
            User(),
            default);

        Assert.Empty(capabilities);
        Assert.Equal(0, client.ConnectCount);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task DiscoverAsync_AvailableTool_UsesRuntimeNameAndInvokesOriginalTool()
    {
        var client = new FakeMcpClient
        {
            Tools =
            [
                new McpTool
                {
                    Name = "get-weather",
                    Description = "Gets weather",
                    Schema = "{\"type\":\"object\"}"
                }
            ]
        };
        var source = Source(client, (_, _) => true);

        var capabilities = await source.DiscoverAsync(
            "agent",
            Config(),
            User(),
            default);
        var capability = Assert.Single(capabilities);
        string result = await capability.Invoke(
            new Dictionary<string, object?> { ["city"] = "Shanghai" },
            default);

        Assert.Equal("mcp__weather__get_weather", capability.Name);
        Assert.Equal("weather/get-weather", capability.ResourceId);
        Assert.Equal("get-weather", client.LastToolName);
        Assert.Equal("Shanghai", client.LastArguments?["city"]);
        Assert.Equal("ok", result);
        await source.DisposeAsync();
        Assert.True(client.Disposed);
    }

    private static McpCapabilitySource Source(
        FakeMcpClient client,
        Func<AgentAuthorizationRequest, IAgentUserContext, bool> authorize)
    {
        var gate = new AgentAuthorizationGate(
            new FakeAuthorizationService(authorize),
            new LlmRegistry());
        return new McpCapabilitySource(
            new FakeMcpClientFactory(client),
            gate,
            NullLogger<McpCapabilitySource>.Instance);
    }

    private static AgentConfig Config() => new()
    {
        Mcp = new McpConfig
        {
            Servers =
            [
                new McpServerConfig
                {
                    Name = "weather",
                    Url = "https://mcp.example.test",
                    Type = McpServerType.Http
                }
            ]
        }
    };

    private static AgentUserContext User() => new()
    {
        UserId = "user",
        TenantId = "tenant",
        IsAuthenticated = true
    };

    private sealed class FakeMcpClientFactory(FakeMcpClient client) : IMcpClientFactory
    {
        public IMcpClient Create() => client;
    }

    private sealed class FakeMcpClient : IMcpClient, IAsyncDisposable
    {
        public List<McpTool> Tools { get; init; } = [];
        public bool IsConnected { get; private set; }
        public string? NegotiatedProtocolVersion { get; private set; }
        public int ConnectCount { get; private set; }
        public string? LastToolName { get; private set; }
        public Dictionary<string, object>? LastArguments { get; private set; }
        public bool Disposed { get; private set; }

        public Task ConnectAsync(
            string serverUrl,
            McpServerType type = McpServerType.Http,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            IsConnected = true;
            NegotiatedProtocolVersion = "2025-06-18";
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Tools);

        public Task<string> CallToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            LastToolName = toolName;
            LastArguments = arguments;
            return Task.FromResult("ok");
        }

        public Task<Stream> ReadResourceAsync(
            string resourceUri,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(Stream.Null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAuthorizationService(
        Func<AgentAuthorizationRequest, IAgentUserContext, bool> authorize)
        : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(authorize(request, userContext));
    }
}
