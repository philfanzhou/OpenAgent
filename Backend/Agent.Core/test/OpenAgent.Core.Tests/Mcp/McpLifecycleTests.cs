using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Mcp;

public sealed class McpLifecycleTests
{
    [Fact]
    public void McpClient_ExposesSingleDependencyInjectionConstructor()
    {
        var constructors = typeof(McpClient).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Single(constructors);
        Assert.Equal(
            new[] { typeof(IHttpClientFactory), typeof(ILogger<McpClient>), typeof(ILoggerFactory) },
            constructors[0].GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task InvokeAsync_ToolMissing_ReturnsNotFoundError()
    {
        var invoker = new McpToolInvoker(
            new McpSessionState(),
            new McpToolCatalog(),
            NullLogger<McpClient>.Instance);

        var result = await invoker.InvokeAsync(
            "missing",
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.Equal("Error: Tool 'missing' not found.", result);
    }

    [Fact]
    public async Task InvokeAsync_ClientDisconnected_ReturnsConnectionError()
    {
        var catalog = new McpToolCatalog();
        catalog.Replace([new McpTool { Name = "lookup" }]);
        var invoker = new McpToolInvoker(
            new McpSessionState(),
            catalog,
            NullLogger<McpClient>.Instance);

        var result = await invoker.InvokeAsync(
            "lookup",
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.Equal("Error: MCP Client not connected.", result);
    }

    [Fact]
    public async Task ConnectAsync_InvalidUrl_WrapsUriFailure()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var state = new McpSessionState();
        var catalog = new McpToolCatalog();
        var connection = new McpConnection(
            state,
            catalog,
            new McpTransportFactory(httpClientFactory.Object, NullLoggerFactory.Instance),
            NullLogger<McpClient>.Instance,
            NullLoggerFactory.Instance);

        var exception = await Assert.ThrowsAsync<ConnectionException>(() =>
            connection.ConnectAsync(
                "not-an-absolute-url",
                McpServerType.SSE,
                CancellationToken.None));

        Assert.IsType<UriFormatException>(exception.InnerException);
    }

    [Fact]
    public async Task DisconnectAsync_ExistingCatalog_ClearsTools()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var state = new McpSessionState();
        var catalog = new McpToolCatalog();
        catalog.Replace([new McpTool { Name = "lookup" }]);
        var connection = new McpConnection(
            state,
            catalog,
            new McpTransportFactory(httpClientFactory.Object, NullLoggerFactory.Instance),
            NullLogger<McpClient>.Instance,
            NullLoggerFactory.Instance);

        await connection.DisconnectAsync(CancellationToken.None);

        Assert.Empty(catalog.List());
    }
}
