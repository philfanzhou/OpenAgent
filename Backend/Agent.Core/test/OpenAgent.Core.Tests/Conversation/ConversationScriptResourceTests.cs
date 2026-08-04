using OpenAgent.Core.Conversation.Scripts;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class ConversationScriptResourceTests
{
    private static readonly string[] ExpectedResourceNames =
    [
        "OpenAgent.Core.Conversation.Repository.Scripts.SqlServerSchema.sql",
        "OpenAgent.Core.Conversation.Repository.Scripts.SqliteSchema.sql",
        "OpenAgent.Core.Conversation.Scripts.AppendMessages.lua",
        "OpenAgent.Core.Conversation.Scripts.LockExtend.lua",
        "OpenAgent.Core.Conversation.Scripts.LockRelease.lua"
    ];

    [Fact]
    public void Manifest_ContainsAllConversationScriptsWithStableNames()
    {
        var actual = typeof(ConversationScripts).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains("Conversation.Scripts", StringComparison.Ordinal)
                || name.Contains("Conversation.Repository.Scripts", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedResourceNames, actual);
    }

    [Fact]
    public void AppendMessages_ContainsAtomicVersionAndIdempotencyLogic()
    {
        Assert.Contains("tonumber(record.version)", ConversationScripts.AppendMessages, StringComparison.Ordinal);
        Assert.Contains("existingKeys", ConversationScripts.AppendMessages, StringComparison.Ordinal);
        Assert.Contains("redis.call('SET'", ConversationScripts.AppendMessages, StringComparison.Ordinal);
    }

    [Fact]
    public void LockScripts_ValidateOwnerBeforeMutation()
    {
        Assert.Contains("current == ARGV[1]", ConversationScripts.LockRelease, StringComparison.Ordinal);
        Assert.Contains("redis.call('DEL'", ConversationScripts.LockRelease, StringComparison.Ordinal);
        Assert.Contains("current == ARGV[1]", ConversationScripts.LockExtend, StringComparison.Ordinal);
        Assert.Contains("redis.call('PEXPIRE'", ConversationScripts.LockExtend, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositorySchemas_ContainRequiredTables()
    {
        Assert.Contains("CREATE TABLE ConversationMessagesArchive", ConversationScripts.SqlServerSchema, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE dbo.ConversationMessageType", ConversationScripts.SqlServerSchema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS ConversationRecords", ConversationScripts.SqliteSchema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS ConversationMessages", ConversationScripts.SqliteSchema, StringComparison.Ordinal);
    }
}
