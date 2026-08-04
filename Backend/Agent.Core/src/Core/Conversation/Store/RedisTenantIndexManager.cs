using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using StackExchange.Redis;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class RedisTenantIndexManager
{
    private readonly TimeSpan _timeToLive;

    internal RedisTenantIndexManager(IOptions<ConversationStoreOptions> options)
    {
        _timeToLive = TimeSpan.FromMinutes(options.Value.RedisTtlMinutes + 10);
    }

    internal async Task AddAndRenewAsync(IDatabase database, string tenantId, string conversationId)
    {
        var indexKey = BuildTenantIndexKey(tenantId);
        await database.SetAddAsync(indexKey, conversationId).ConfigureAwait(false);
        await database.KeyExpireAsync(indexKey, _timeToLive).ConfigureAwait(false);
    }

    internal Task RenewAsync(IDatabase database, string tenantId)
    {
        return database.KeyExpireAsync(BuildTenantIndexKey(tenantId), _timeToLive);
    }

    internal static string BuildTenantIndexKey(string tenantId) => $"conversation-index:{tenantId}";
}
