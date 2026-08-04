using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal interface IMcpClientFactory
{
    IMcpClient Create();
}

internal sealed class McpClientFactory(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IMcpClientFactory
{
    public IMcpClient Create() => new McpClient(
        httpClientFactory,
        loggerFactory.CreateLogger<McpClient>(),
        loggerFactory);
}
