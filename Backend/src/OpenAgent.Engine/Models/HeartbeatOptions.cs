namespace OpenAgent.Engine.Models;

internal class HeartbeatOptions
{
    public int IntervalSeconds { get; set; } = 10;
    public int RetryDelaySeconds { get; set; } = 5;
    public int RegistryTtlSeconds { get; set; } = 30;
    public string? AdvertisedHost { get; set; }
    public int? AdvertisedPort { get; set; }
    /// <summary>When set, the Engine registers through the Router's HTTP endpoint instead of writing Redis.</summary>
    public string? RouterUrl { get; set; }
    public string? RegistrationKey { get; set; }
    public string[] Intents { get; set; } = ["chat"];
}
