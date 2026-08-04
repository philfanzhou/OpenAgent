using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Contracts.Configuration;
using Xunit;

namespace OpenAgent.Core.Tests.Mcp;

public sealed class McpClientSdkTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/mcp")]
    [InlineData("/custom/path")]
    public async Task ConnectAsync_OfficialStreamableHttpEndpoint_DiscoversTools(string route)
    {
        await using McpHttpServerFixture server = await McpHttpServerFixture.StartAsync(route);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient());
        await using var client = new McpClient(
            httpClientFactory.Object,
            NullLogger<McpClient>.Instance,
            NullLoggerFactory.Instance);

        await client.ConnectAsync(server.Endpoint.AbsoluteUri, McpServerType.Http, CancellationToken.None);

        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal("lookup", tool.Name);
        Assert.Equal("Lookup data", tool.Description);
    }

    [Fact]
    public async Task ConnectAsync_InvalidUrl_WrapsConnectionFailure()
    {
        await using var fixture = new McpClientFixture();

        var exception = await Assert.ThrowsAsync<ConnectionException>(() =>
            fixture.Client.ConnectAsync("not-an-absolute-url", McpServerType.SSE, CancellationToken.None));

        Assert.IsType<UriFormatException>(exception.InnerException);
    }

    [Fact]
    public async Task ConnectAsync_OfficialSseToolMetadata_MapsStandardAnnotations()
    {
        await using var fixture = new McpClientFixture();
        await fixture.Client.ConnectAsync("http://mcp.test", McpServerType.SSE, CancellationToken.None);

        var tool = Assert.Single(await fixture.Client.ListToolsAsync());

        Assert.Equal("lookup", tool.Name);
        Assert.Equal("Lookup data", tool.Description);
        Assert.True(tool.IsDangerous);
        Assert.True(fixture.Client.IsConnected);
    }

    [Fact]
    public async Task CallToolAsync_OfficialSseResponse_ReturnsTextContent()
    {
        await using var fixture = new McpClientFixture();
        await fixture.Client.ConnectAsync("http://mcp.test", McpServerType.SSE, CancellationToken.None);

        var result = await fixture.Client.CallToolAsync(
            "lookup",
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.Equal("sdk-tool-result", result);
    }

    [Fact]
    public async Task ReadResourceAsync_OfficialSseResponse_ReturnsUtf8Stream()
    {
        await using var fixture = new McpClientFixture();
        await fixture.Client.ConnectAsync("http://mcp.test", McpServerType.SSE, CancellationToken.None);

        await using var stream = await fixture.Client.ReadResourceAsync(
            "resource://text",
            CancellationToken.None);
        using var reader = new StreamReader(stream);

        Assert.Equal("sdk-resource", await reader.ReadToEndAsync());
    }

    private sealed class McpClientFixture : IAsyncDisposable
    {
        private readonly SdkSseMessageHandler _handler = new();

        public McpClientFixture()
        {
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory
                .Setup(factory => factory.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(_handler, disposeHandler: false));

            Client = new McpClient(
                httpClientFactory.Object,
                NullLogger<McpClient>.Instance,
                NullLoggerFactory.Instance);
        }

        public McpClient Client { get; }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            _handler.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
