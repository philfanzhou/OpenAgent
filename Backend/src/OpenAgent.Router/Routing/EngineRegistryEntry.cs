namespace OpenAgent.Router;

public sealed class EngineRegistryEntry
{
    public string EngineId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int Load { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public string[] Intents { get; set; } = [];
    public string[] Capabilities { get; set; } = [];
}
