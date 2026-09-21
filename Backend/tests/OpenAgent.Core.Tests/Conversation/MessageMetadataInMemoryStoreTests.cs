using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation.Store;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

/// <summary>
/// 三存储一致性矩阵的 InMemory 腿：直存不经序列化，
/// 全字段 + 未知键的强类型元数据必须原样保真（EF/Redis 腿见 Infrastructure.Tests）。
/// </summary>
public sealed class MessageMetadataInMemoryStoreTests
{
    [Fact]
    public async Task AppendThenRead_FullMetadata_DeepEqualsWithoutSerialization()
    {
        ConversationMessageMetadata metadata = new()
        {
            Files = [new MessageFileMetadata("file-1", "notes.md", "text/markdown", 12, "files/t/f-1")],
            Reasoning = "thinking",
            ExecutionStatus = "Cancelled",
            ToolArguments = "{\"a\":1}",
            Extensions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Custom"] = "kept"
            }
        };
        InMemoryConversationStore store = new(new FakeUserContext());
        Assert.True(await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            Version = 1
        }));

        AppendResult append = await store.AppendMessagesAsync(
            "tenant-1",
            "conversation-1",
            1,
            [new ConversationMessage
            {
                MessageId = "message-1",
                Sequence = 1,
                Role = "assistant",
                Content = "partial",
                Timestamp = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
                Metadata = metadata
            }]);
        Assert.True(append.Success);

        ConversationRecord? record = await store.GetRecordAsync("tenant-1", "conversation-1");
        ConversationMessage message = Assert.Single(record!.Messages);

        Assert.Same(metadata, message.Metadata);
        Assert.Equal("file-1", Assert.Single(message.Metadata.Files!).FileId);
        Assert.Equal("thinking", message.Metadata.Reasoning);
        Assert.Equal("Cancelled", message.Metadata.ExecutionStatus);
        Assert.Equal("{\"a\":1}", message.Metadata.ToolArguments);
        Assert.Equal("kept", message.Metadata.Extensions!["Custom"]);
    }

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public string UserId => "user-1";
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }
}
