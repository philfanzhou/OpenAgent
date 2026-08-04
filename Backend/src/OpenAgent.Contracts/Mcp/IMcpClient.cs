namespace OpenAgent.Contracts.Mcp;

public class McpTool
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public bool IsDangerous { get; set; } = false;
}

public interface IMcpClient
{
    Task ConnectAsync(string serverUrl, Configuration.McpServerType type = Configuration.McpServerType.Http, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> CallToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default);
    Task<Stream> ReadResourceAsync(string resourceUri, CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}
