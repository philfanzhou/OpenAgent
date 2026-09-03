using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
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
            new McpTransportFactory(httpClients.Object, NullLoggerFactory.Instance),
            new AgentAuthorizationGate(new AllowAllAgentAuthorizationService()),
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

    [Fact]
    public async Task CreateAsync_EmptyTenantServer_TreatedAsGloballyAvailable()
    {
        var httpClients = new Mock<IHttpClientFactory>();
        var factory = new McpToolFactory(
            new McpTransportFactory(httpClients.Object, NullLoggerFactory.Instance),
            new AgentAuthorizationGate(new AllowAllAgentAuthorizationService()),
            new McpRegistry(),
            NullLoggerFactory.Instance,
            NullLogger<McpToolFactory>.Instance);
        var config = new McpConfig
        {
            Servers =
            [
                new McpServerConfig
                {
                    TenantId = "",
                    Name = "legacy-server",
                    Url = "http://127.0.0.1:1/mcp"
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

        // 连接会失败（端口 1 拒绝连接），但租户过滤必须放行空租户服务器：
        // 通过 transport 创建时是否调用了 HttpClient 工厂来断言。
        Assert.Empty(runtime.Tools);
        httpClients.Verify(client => client.CreateClient(It.IsAny<string>()), Times.Once);
    }
}
