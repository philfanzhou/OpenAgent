using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal interface IMcpClientFactory
{
    IMcpClient Create();
}

internal sealed class McpServerClientFactory(ILoggerFactory loggerFactory) : IMcpClientFactory
{
    public IMcpClient Create() => new McpServerClient(loggerFactory);
}
