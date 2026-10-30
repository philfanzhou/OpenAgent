using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenAgent.Framework.Configuration;

/// <summary>Best-effort post-commit notifications; TTL remains the recovery path after message loss.</summary>
internal sealed partial class ConfigurationInvalidation(IConnectionMultiplexer? redis,
    ILogger<ConfigurationInvalidation> logger) : IHostedService
{
    private static readonly RedisChannel Channel = RedisChannel.Literal("openagent:configuration:invalidate:v1");
    internal event Action<string, string, string>? Changed;

    internal async Task PublishAsync(string kind, string tenantId, string id)
    {
        Changed?.Invoke(kind, tenantId, id);
        if (redis is null) return;
        try
        {
            await redis.GetSubscriber().PublishAsync(Channel, JsonSerializer.Serialize(new Notice(kind, tenantId, id))).ConfigureAwait(false);
        }
        catch (RedisException) { NotificationUnavailable(logger); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (redis is null) return;
        try
        {
            await redis.GetSubscriber().SubscribeAsync(Channel, (_, payload) =>
            {
                try
                {
                    Notice? notice = JsonSerializer.Deserialize<Notice>(payload.ToString());
                    if (notice is { Kind: "mcp" or "runtime" } && !string.IsNullOrWhiteSpace(notice.TenantId)
                        && !string.IsNullOrWhiteSpace(notice.Id)) Changed?.Invoke(notice.Kind, notice.TenantId, notice.Id);
                }
                catch (JsonException) { }
            }).ConfigureAwait(false);
        }
        catch (RedisException) { NotificationUnavailable(logger); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (redis is null) return;
        try { await redis.GetSubscriber().UnsubscribeAsync(Channel).ConfigureAwait(false); }
        catch (RedisException) { NotificationUnavailable(logger); }
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Warning, Message = "Configuration invalidation notification unavailable; cached reads expire by TTL.")]
    private static partial void NotificationUnavailable(ILogger logger);

    private sealed record Notice(string Kind, string TenantId, string Id);
}
