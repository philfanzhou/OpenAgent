namespace OpenAgent.Router.Options;

internal sealed class RouterCacheSettings
{
    private const int DefaultMaxRequestBodyBytes = 1024 * 1024;
    private const int DefaultMaxResponseBodyBytes = 4 * 1024 * 1024;

    internal RouterCacheSettings(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("RouterSettings:Caching");
        MaxRequestBodyBytes = Math.Clamp(
            section.GetValue("MaxRequestBodyBytes", DefaultMaxRequestBodyBytes),
            1024,
            16 * 1024 * 1024);
        MaxResponseBodyBytes = Math.Clamp(
            section.GetValue("MaxResponseBodyBytes", DefaultMaxResponseBodyBytes),
            1024,
            32 * 1024 * 1024);
        IdempotencyTimeToLive = TimeSpan.FromSeconds(Math.Clamp(
            section.GetValue("IdempotencyTtlSeconds", 86400),
            60,
            7 * 24 * 60 * 60));
        IdempotencyPendingTimeToLive = TimeSpan.FromSeconds(Math.Clamp(
            section.GetValue("IdempotencyPendingTtlSeconds", 120),
            5,
            15 * 60));
        QueryTimeToLive = TimeSpan.FromSeconds(Math.Clamp(
            section.GetValue("QueryTtlSeconds", 300),
            1,
            24 * 60 * 60));
    }

    internal int MaxRequestBodyBytes { get; }

    internal int MaxResponseBodyBytes { get; }

    internal TimeSpan IdempotencyTimeToLive { get; }

    internal TimeSpan IdempotencyPendingTimeToLive { get; }

    internal TimeSpan QueryTimeToLive { get; }
}
