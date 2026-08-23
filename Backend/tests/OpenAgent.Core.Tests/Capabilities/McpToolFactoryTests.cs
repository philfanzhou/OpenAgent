using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Models;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class McpToolFactoryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2026-07-28", "2026-07-28")]
    public void CreateClientOptions_UsesOfficialMcpClientOptions(
        string? configured,
        string? expected)
    {
        McpClientOptions options = McpToolFactory.CreateClientOptions(
            new McpServerConfig { ProtocolVersion = configured });

        Assert.Equal(expected, options.ProtocolVersion);
        Assert.Equal("OpenAgent", options.ClientInfo?.Name);
    }

    [Fact]
    public async Task CreateAsync_DifferentTenant_DoesNotExposeMcpTools()
    {
        var httpClients = new Mock<IHttpClientFactory>();
        var factory = new McpToolFactory(
            new McpTransportFactory(
                httpClients.Object,
                NullLoggerFactory.Instance,
                Options.Create(new McpExecutionOptions())),
            new AgentAuthorizationGate(
                new AllowAllAgentAuthorizationService(),
                new LlmRegistry()),
            new McpRegistry(),
            NullLoggerFactory.Instance,
            NullLogger<McpToolFactory>.Instance);
        var config = new McpConfig
        {
            Servers =
            [
                new McpServerConfig
                {
                    TenantId = "tenant-b",
                    Name = "private-server",
                    Url = "https://mcp.example.com"
                }
            ]
        };

        await using McpToolRuntime runtime = await factory.CreateAsync(
            "agent-1",
            config,
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-a",
                IsAuthenticated = true
            },
            CancellationToken.None);

        Assert.Empty(runtime.Tools);
        httpClients.VerifyNoOtherCalls();
    }
}
