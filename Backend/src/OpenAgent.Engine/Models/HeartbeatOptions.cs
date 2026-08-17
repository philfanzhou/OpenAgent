namespace OpenAgent.Engine.Models;

internal class HeartbeatOptions
{
    public int IntervalSeconds { get; set; } = 10;
    public int RetryDelaySeconds { get; set; } = 5;
    public int RegistryTtlSeconds { get; set; } = 30;
    public string? AdvertisedHost { get; set; }
    public int? AdvertisedPort { get; set; }
    public string[] Intents { get; set; } = ["chat"];
    public string[] Capabilities { get; set; } = [];
}
