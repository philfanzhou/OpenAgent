using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal interface IMcpClientFactory
{
    IMcpClient Create();
}

internal sealed class McpClientFactory(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    IOptions<McpExecutionOptions> options) : IMcpClientFactory
{
    public IMcpClient Create() => new McpClient(
        httpClientFactory,
        loggerFactory.CreateLogger<McpClient>(),
        loggerFactory,
        options);
}
