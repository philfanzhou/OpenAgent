using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpSessionState
{
    internal SemaphoreSlim ConnectionLock { get; } = new(1, 1);
    internal SdkMcpClient? Client { get; private set; }
    internal string ServerUrl { get; private set; } = string.Empty;

    internal bool IsConnected => Client is { Completion.IsCompleted: false };

    internal void Activate(SdkMcpClient client, string serverUrl)
    {
        Client = client;
        ServerUrl = serverUrl;
    }

    internal SdkMcpClient? Detach()
    {
        SdkMcpClient? client = Client;
        Client = null;
        ServerUrl = string.Empty;
        return client;
    }
}
