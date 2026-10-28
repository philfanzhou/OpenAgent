using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using StackExchange.Redis;

namespace OpenAgent.Infrastructure;

/// <summary>
/// Redis implementation of the optional conversation hot cache. It contains a
/// complete serialized record so a cache hit avoids database reads.
/// </summary>
internal sealed class RedisConversationCache(
    IConnectionMultiplexer connection,
    IOptions<ConversationCacheOptions> options) : IConversationCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TimeSpan _timeToLive = TimeSpan.FromMinutes(
        Math.Max(1, options.Value.TimeToLiveMinutes));

    public async Task<ConversationRecord?> GetAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        RedisValue value = await connection.GetDatabase().StringGetAsync(BuildKey(tenantId, conversationId))
            .ConfigureAwait(false);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<ConversationRecord>(value!, JsonOptions);
    }

    public Task SetAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
        connection.GetDatabase().StringSetAsync(
            BuildKey(record.TenantId, record.ConversationId),
            JsonSerializer.Serialize(record, JsonOptions),
            _timeToLive);

    public Task RemoveAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
        connection.GetDatabase().KeyDeleteAsync(BuildKey(tenantId, conversationId));

    private static string BuildKey(string tenantId, string conversationId) =>
        $"openagent:conversation:{tenantId}:{conversationId}";
}
