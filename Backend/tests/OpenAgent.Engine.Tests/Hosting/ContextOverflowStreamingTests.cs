using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Conversation;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>
/// 生产缺陷形态：请求超出模型上下文窗口时 provider 抛错，整轮执行中止。
/// 旧行为只有一个带英文 provider 原文的 SSE error 事件 + 前端"响应未完成"占位——
/// 错误原因既不分类也不持久化，刷新后完全丢失。这里用进程内真实宿主验证：
/// error 事件给出可执行的中文指引，失败原因随消息持久化（metadata.error），
/// 刷新后前端仍能展示失败原因与 TraceId。
/// </summary>
public sealed class ContextOverflowStreamingTests
{
    [Fact]
    public async Task Stream_ProviderContextOverflow_ClassifiedErrorEventAndPersistedReason()
    {
        var provider = new ThrowingChatClient(new ClientResultException(
            "HTTP 400 (invalid_request_error: This model's maximum context length is 8192 tokens, "
            + "however you requested 10000 tokens. Please reduce the length of the messages.)",
            (PipelineResponse)null!,
            null!));
        await using StreamingHost host = await StreamingHost.StartAsync(provider);

        List<(string Event, JsonElement Data)> frames =
            await host.PostStreamAsync("context-overflow-conversation");

        (string _, JsonElement errorData) = Assert.Single(frames, frame => frame.Event == "error");
        Assert.Equal("超出模型上下文窗口", errorData.GetProperty("title").GetString());
        Assert.EndsWith("context-length-exceeded", errorData.GetProperty("type").GetString());
        string? detail = errorData.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.Contains("新会话", detail);
        // provider 原始错误体不透出
        Assert.DoesNotContain("however you requested", detail);

        (string _, JsonElement doneData) = Assert.Single(frames, frame => frame.Event == "done");
        Assert.Equal("error", doneData.GetProperty("status").GetString());

        // 失败原因随消息持久化：前端刷新重载后仍能看到，而不是退化为"响应未完成"。
        ConversationRecord? record = await host.Store.GetRecordAsync(
            "tenant-1", "context-overflow-conversation");
        Assert.NotNull(record);
        ConversationMessage? assistantRow = record.Messages?.LastOrDefault(
            row => row.Role == "assistant");
        Assert.NotNull(assistantRow);
        Assert.Equal("Failed", assistantRow!.Metadata?.ExecutionStatus);
        Assert.Equal("超出模型上下文窗口", assistantRow.Metadata?.Error?.Title);
        Assert.Contains("新会话", assistantRow.Metadata?.Error?.Detail);
        Assert.False(string.IsNullOrWhiteSpace(assistantRow.Metadata?.Error?.TraceId));
    }

    /// <summary>按请求抛出固定异常的最小 IChatClient：模拟 provider 直接拒绝请求。</summary>
    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw exception;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            // async 迭代器语法要求 yield：实际效果是异步抛出，与生产 provider 行为一致。
            yield return Fail();
        }

        private ChatResponseUpdate Fail() => throw exception;

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
