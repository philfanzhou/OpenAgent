using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Repository;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class SqlServerConversationRepositoryTests
{
    [Fact]
    public void Constructor_throws_when_connection_string_missing()
    {
        var options = Options.Create(new ConversationStoreOptions
        {
            ColdArchiveConnectionString = null
        });

        Assert.Throws<InvalidOperationException>(() =>
            new SqlServerConversationRepository(
                options,
                NullLogger<SqlServerConversationRepository>.Instance,
                new ConversationStoreMetrics()));
    }
}
