namespace OpenAgent.Engine.Models;

internal class RegistryEntry
{
    public string EngineId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int Load { get; set; }
    public DateTime LastHeartbeat { get; set; }
}
