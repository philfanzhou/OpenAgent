using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Models;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class McpConnectionTesterTests
{
    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public async Task TestAsync_PinnedProtocolVersion_ReturnsNegotiatedVersion(string protocolVersion)
    {
        var client = new FakeMcpClient();
        var gate = new AgentAuthorizationGate(new AllowAuthorizationService(), new LlmRegistry());
        var tester = new McpConnectionTester(
            new FakeMcpClientFactory(client),
            gate,
            Options.Create(new McpExecutionOptions()));
        var request = new McpConnectionTestRequest
        {
            AgentId = "support",
            Server = new McpServerConfig
            {
                Name = "tools",
                Url = "https://mcp.example.test/mcp",
                ProtocolVersion = protocolVersion
            }
        };

        McpConnectionTestResult result = await tester.TestAsync(
            request,
            new AgentUserContext { UserId = "user", TenantId = "tenant", IsAuthenticated = true },
            "trace",
            default);

        Assert.True(result.Success);
        Assert.Equal(protocolVersion, client.RequestedProtocolVersion);
        Assert.Equal(protocolVersion, result.RequestedProtocolVersion);
        Assert.Equal(protocolVersion, result.NegotiatedProtocolVersion);
    }

    private sealed class FakeMcpClientFactory(FakeMcpClient client) : IMcpClientFactory
    {
        public IMcpClient Create() => client;
    }

    private sealed class FakeMcpClient : IMcpClient, IAsyncDisposable
    {
        public bool IsConnected { get; private set; }
        public string? NegotiatedProtocolVersion { get; private set; }
        public string? RequestedProtocolVersion { get; private set; }

        public Task ConnectAsync(
            string serverUrl,
            McpServerType type = McpServerType.Http,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ConnectAsync(McpServerConfig server, CancellationToken cancellationToken = default)
        {
            RequestedProtocolVersion = server.ProtocolVersion;
            NegotiatedProtocolVersion = server.ProtocolVersion;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpTool> { new() { Name = "echo" } });

        public Task<string> CallToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult("ok");

        public Task<Stream> ReadResourceAsync(
            string resourceUri,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream>(Stream.Null);

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllowAuthorizationService : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
