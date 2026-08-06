using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal interface IMcpClientFactory
{
    IMcpClient Create();
}

internal sealed class McpServerClientFactory(
    ILoggerFactory loggerFactory,
    McpTransportFactory transportFactory) : IMcpClientFactory
{
    public IMcpClient Create() => new McpServerClient(loggerFactory, transportFactory);
}
