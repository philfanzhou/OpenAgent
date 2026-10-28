namespace OpenAgent.Router.Options;

internal enum RateLimitFailureMode
{
    Local,
    FailOpen,
    FailClosed
}

internal sealed record RateLimitSettings(
    double RequestsPerSecond,
    double BurstCapacity,
    RateLimitFailureMode FailureMode)
{
    internal const string SectionName = "RouterSettings:RateLimiting";

    internal static RateLimitSettings FromConfiguration(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(SectionName);
        return new(
            Math.Max(section.GetValue("RequestsPerSecond", 100d), 0.001d),
            Math.Max(section.GetValue("BurstCapacity", 200d), 1d),
            Enum.TryParse(section["FailureMode"], true, out RateLimitFailureMode mode)
                ? mode
                : RateLimitFailureMode.Local);
    }
}
