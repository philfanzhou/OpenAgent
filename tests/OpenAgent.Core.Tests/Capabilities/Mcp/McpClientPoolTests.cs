using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class McpClientPoolTests
{
    [Fact]
    public async Task AcquireAsync_UnreachableServer_ThrowsAndDoesNotCacheFailure()
    {
        // 连接失败必须上抛（由 McpToolFactory 按服务器隔离），且不得把失败状态
        // 缓存成"已连接"：第二次获取要重新建连（再次调用 HttpClient 工厂）。
        var httpClients = new Mock<IHttpClientFactory>();
        httpClients.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());
        await using var pool = new McpClientPool(
            new McpTransportFactory(httpClients.Object, NullLoggerFactory.Instance),
            NullLoggerFactory.Instance,
            Options.Create(new McpExecutionOptions()));
        var server = new McpServerConfig
        {
            Name = "dead-server",
            // 127.0.0.1:9（discard 端口）在本机立即拒绝连接。
            Url = "https://127.0.0.1:9/mcp",
            Type = McpServerType.Http
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            pool.AcquireAsync(server, User, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            pool.AcquireAsync(server, User, CancellationToken.None));

        httpClients.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidateAsync_UnknownServer_IsNoOp()
    {
        var httpClients = new Mock<IHttpClientFactory>();
        await using var pool = new McpClientPool(
            new McpTransportFactory(httpClients.Object, NullLoggerFactory.Instance),
            NullLoggerFactory.Instance,
            Options.Create(new McpExecutionOptions()));

        // 不存在的键：安静返回，不创建连接，也不抛异常。
        await pool.InvalidateAsync(
            new McpServerConfig { Name = "ghost", Url = "https://ghost.example.com", Type = McpServerType.Http },
            User);

        httpClients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisposeAsync_EmptyPool_CompletesQuietly()
    {
        var pool = new McpClientPool(
            new McpTransportFactory(Mock.Of<IHttpClientFactory>(), NullLoggerFactory.Instance),
            NullLoggerFactory.Instance,
            Options.Create(new McpExecutionOptions()));

        await pool.DisposeAsync();
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };
}
