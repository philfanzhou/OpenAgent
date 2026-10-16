namespace OpenAgent.Router.Options;

internal enum RedisDiscoveryFailureMode
{
    StaticOnly,
    LastKnown
}

internal sealed record ServiceDiscoverySettings(
    TimeSpan RefreshInterval,
    TimeSpan HeartbeatStaleAfter,
    TimeSpan SnapshotMaxAge,
    RedisDiscoveryFailureMode RedisFailureMode)
{
    internal const string SectionName = "RouterSettings:ServiceDiscovery";

    internal static ServiceDiscoverySettings FromConfiguration(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(SectionName);
        return new(
            TimeSpan.FromSeconds(Math.Max(section.GetValue("RefreshIntervalSeconds", 5), 1)),
            TimeSpan.FromSeconds(Math.Max(section.GetValue("HeartbeatStaleAfterSeconds", 60), 1)),
            TimeSpan.FromSeconds(Math.Max(section.GetValue("SnapshotMaxAgeSeconds", 15), 1)),
            Enum.TryParse(section["RedisFailureMode"], true, out RedisDiscoveryFailureMode mode)
                ? mode
                : RedisDiscoveryFailureMode.StaticOnly);
    }
}
