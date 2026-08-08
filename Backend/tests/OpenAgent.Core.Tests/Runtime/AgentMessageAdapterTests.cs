using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class AgentMessageAdapterTests
{
    [Fact]
    public void FromStored_ToolCall_PreservesCallIdAndToolName()
    {
        ConversationMessage stored = new()
        {
            MessageId = "message-1",
            Sequence = 1,
            Role = "assistant",
            Content = string.Empty,
            ToolCallId = "call-1",
            ToolName = "search"
        };

        ChatMessage? restored = AgentMessageAdapter.FromStored(stored);

        FunctionCallContent call = Assert.Single(
            restored!.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("search", call.Name);
    }

    [Fact]
    public void BuildAttachmentMetadata_PreservesObjectReferenceWithoutBytes()
    {
        IReadOnlyDictionary<string, string>? metadata = AgentMessageAdapter.BuildAttachmentMetadata(
        [
            new AgentAttachment
            {
                FileName = "notes.txt",
                MediaType = "text/plain",
                Data = [1, 2, 3],
                ObjectKey = "attachments/object.txt",
                Sha256 = "sha-256"
            }
        ]);

        using JsonDocument document = JsonDocument.Parse(metadata!["Attachments"]);
        JsonElement attachment = document.RootElement[0];
        Assert.Equal("attachments/object.txt", attachment.GetProperty("ObjectKey").GetString());
        Assert.Equal("sha-256", attachment.GetProperty("Sha256").GetString());
        Assert.False(attachment.TryGetProperty("Data", out _));
    }
}
