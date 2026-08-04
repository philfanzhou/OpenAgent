using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Mcp;

public class McpComponentTests
{
    [Fact]
    public void McpResourceReader_DecodesBase64AndTextContent()
    {
        var blob = BlobResourceContents.FromBytes(
            Encoding.UTF8.GetBytes("hello"),
            "blob://sample",
            "text/plain");
        var text = new TextResourceContents
        {
            Uri = "text://sample",
            Text = "world"
        };

        using var blobStream = McpResourceReader.CreateStream(blob, "blob://sample");
        using var textStream = McpResourceReader.CreateStream(text, "text://sample");
        using var blobReader = new StreamReader(blobStream, Encoding.UTF8);
        using var textReader = new StreamReader(textStream, Encoding.UTF8);

        Assert.Equal("hello", blobReader.ReadToEnd());
        Assert.Equal("world", textReader.ReadToEnd());
    }

    [Fact]
    public void McpToolCatalog_ReplacesAndSnapshotsTools()
    {
        var catalog = new McpToolCatalog();
        catalog.Replace([
            new McpTool { Name = "first" },
            new McpTool { Name = "second" }
        ]);

        var snapshot = catalog.List();
        catalog.Clear();

        Assert.Equal(2, snapshot.Count);
        Assert.Empty(catalog.List());
    }

    [Theory]
    [InlineData("http://mcp.test", McpServerType.Http, "http://mcp.test", HttpTransportMode.StreamableHttp)]
    [InlineData("http://mcp.test", McpServerType.SSE, "http://mcp.test/sse", HttpTransportMode.Sse)]
    [InlineData("http://mcp.test/mcp", McpServerType.Http, "http://mcp.test/mcp", HttpTransportMode.StreamableHttp)]
    [InlineData("http://mcp.test/sse", McpServerType.SSE, "http://mcp.test/sse", HttpTransportMode.Sse)]
    [InlineData("http://mcp.test/custom/path", McpServerType.Http, "http://mcp.test/custom/path", HttpTransportMode.StreamableHttp)]
    public void McpTransportFactory_ResolveEndpoint_MapsConfiguredTransport(
        string serverUrl,
        McpServerType type,
        string expectedEndpoint,
        HttpTransportMode expectedMode)
    {
        var (endpoint, mode) = McpTransportFactory.ResolveEndpoint(serverUrl, type);

        Assert.Equal(expectedEndpoint, endpoint.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(expectedMode, mode);
    }
}
