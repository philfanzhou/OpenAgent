using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class AgentExecutorToolLoopTests
{
    [Fact]
    public async Task ExecuteStreamingAsync_KimiStyleToolCallId_PairsResultInFollowupRequest()
    {
        // Moonshot/Kimi returns tool call ids like "read_file:0". A gateway rejects the
        // followup request when the assistant tool_call lacks a matching tool response,
        // so the second request must carry both contents with the same id.
        var provider = new SequenceChatProvider(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("read_file:0", "get_current_user_profile")])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, "finished")
            ]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("tool-loop-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(2, provider.Requests.Count);
        IReadOnlyList<ChatMessage> followup = provider.Requests[1];
        FunctionCallContent call = Assert.Single(
            followup.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("read_file:0", call.CallId);
        FunctionResultContent result = Assert.Single(
            followup.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal("read_file:0", result.CallId);
        int callMessageIndex = followup.ToList().FindIndex(
            message => message.Contents.Contains(call));
        int resultMessageIndex = followup.ToList().FindIndex(
            message => message.Contents.Contains(result));
        Assert.True(resultMessageIndex > callMessageIndex, "The tool result must follow its tool call.");
        Assert.Contains(events, item => item.Type == AgentStreamEventType.ToolResult);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_SplitStoredParallelCalls_CoalescedIntoSingleAssistantMessage()
    {
        // Mirrors the production 400: ToStored expands one turn's parallel tool calls
        // into separate assistant rows, and reloading them produced stacked assistant
        // tool_call blocks that Moonshot rejects because the responses come only after
        // the last block. The first follow-up request must carry them merged.
        var provider = new SequenceChatProvider(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("read_file:0", "get_current_user_profile")])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(CreateUsage())]),
                new ChatResponseUpdate(ChatRole.Assistant, "the s3 id is in the metadata")
            ]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider);
        await SeedSplitToolCallHistoryAsync(runtime.Store);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("split-history-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(2, provider.Requests.Count);
        IReadOnlyList<ChatMessage> firstRequest = provider.Requests[0];
        List<ChatMessage> callCarriers = firstRequest
            .Where(message => message.Role == ChatRole.Assistant
                && message.Contents.OfType<FunctionCallContent>().Any())
            .ToList();
        ChatMessage historyCalls = Assert.Single(callCarriers, message =>
            message.Contents.OfType<FunctionCallContent>()
                .Any(call => call.CallId == "read_file_0"));
        Assert.Equal(
            new[] { "read_file_0", "read_file_1", "read_file_2", "read_file_3" },
            historyCalls.Contents.OfType<FunctionCallContent>()
                .Select(call => call.CallId)
                .ToArray());
        for (int index = 0; index < firstRequest.Count - 1; index++)
        {
            bool adjacentBlocks = HasToolCalls(firstRequest[index]) && HasToolCalls(firstRequest[index + 1]);
            Assert.False(adjacentBlocks, "Stacked assistant tool_call blocks are rejected by providers.");
        }
        Assert.Contains(events, item => item.Type == AgentStreamEventType.ToolResult);
    }

    private static bool HasToolCalls(ChatMessage message) =>
        message.Role == ChatRole.Assistant
        && message.Contents.OfType<FunctionCallContent>().Any();

    private static async Task SeedSplitToolCallHistoryAsync(
        OpenAgent.Core.Conversation.Store.InMemoryConversationStore store)
    {
        var rows = new List<ConversationMessage>
        {
            StoredRow(1, "user", "我刚刚上传了些什么", null, null),
            StoredRow(2, "assistant", "让我读取确认一下内容：", "read_file_0", "read_file"),
            StoredRow(3, "assistant", string.Empty, "read_file_1", "read_file"),
            StoredRow(4, "assistant", string.Empty, "read_file_2", "read_file"),
            StoredRow(5, "assistant", string.Empty, "read_file_3", "read_file"),
            StoredRow(6, "tool", "文件读取失败：not a text file.", "read_file_0", null),
            StoredRow(7, "tool", "文件读取失败：not a text file.", "read_file_1", null),
            StoredRow(8, "tool", "文件读取失败：not a text file.", "read_file_2", null),
            StoredRow(9, "tool", "文件读取失败：not a text file.", "read_file_3", null),
            StoredRow(10, "assistant", "您上传了 4 张图片。", null, null)
        };
        await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "split-history-conversation",
            TenantId = User.TenantId!,
            UserId = User.UserId,
            AgentId = "test-agent",
            Type = ConversationType.User,
            Status = ConversationStatus.Completed,
            Messages = rows
        });
    }

    private static ConversationMessage StoredRow(
        int sequence,
        string role,
        string content,
        string? toolCallId,
        string? toolName) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        Sequence = sequence,
        Role = role,
        Content = content,
        ToolCallId = toolCallId,
        ToolName = toolName
    };

    [Fact]
    public async Task ExecuteStreamingAsync_SecondTurnToolCallAfterStoredHistory_PairsResult()
    {
        // Mirrors the failing production shape: first turn stores paired underscore-id
        // tool rows, second turn issues a fresh Kimi-style call with reasoning content.
        var provider = new SequenceChatProvider(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("read_file_0", "get_current_user_profile")])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(CreateUsage())]),
                new ChatResponseUpdate(ChatRole.Assistant, "first answer")
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextReasoningContent("thinking about it"),
                    new FunctionCallContent("read_file:0", "get_current_user_profile")
                ])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(CreateUsage())]),
                new ChatResponseUpdate(ChatRole.Assistant, "second answer")
            ]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("multi-turn-loop"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }
        events.Clear();
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("multi-turn-loop"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(4, provider.Requests.Count);
        IReadOnlyList<ChatMessage> followup = provider.Requests[3];
        FunctionCallContent call = Assert.Single(
            followup.SelectMany(message => message.Contents).OfType<FunctionCallContent>(),
            item => item.CallId == "read_file:0");
        FunctionResultContent result = Assert.Single(
            followup.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            item => item.CallId == "read_file:0");
        List<ChatMessage> messages = followup.ToList();
        int callMessageIndex = messages.FindIndex(message => message.Contents.Contains(call));
        int resultMessageIndex = messages.FindIndex(message => message.Contents.Contains(result));
        Assert.True(callMessageIndex >= 0, "Followup request lost the assistant tool call.");
        Assert.True(resultMessageIndex > callMessageIndex, "The tool result must follow its tool call.");
    }

    private static UsageDetails CreateUsage() => new()
    {
        InputTokenCount = 21,
        OutputTokenCount = 8,
        TotalTokenCount = 29
    };

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "hello",
        AgentId = "test-agent",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };
}
