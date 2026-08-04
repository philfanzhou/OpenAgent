using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation.Repository;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class ConversationStoreComponentsTests
{
    [Fact]
    public void RedisTenantIndexManager_BuildsTenantScopedKey()
    {
        Assert.Equal(
            "conversation-index:tenant-1",
            RedisTenantIndexManager.BuildTenantIndexKey("tenant-1"));
    }

    [Fact]
    public async Task SqlServerRetryPolicy_RetriesWithConfiguredCount()
    {
        var attempts = 0;
        var policy = new SqlServerRetryPolicy(
            retryCount: 2,
            initialDelayMilliseconds: 0,
            NullLogger<SqlServerConversationRepository>.Instance);

        await policy.ExecuteAsync(() =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromException(new InvalidOperationException("transient"))
                : Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(3, attempts);
    }
}
