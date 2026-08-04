using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Engine;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation.Lock;
using OpenAgent.Core.Tests;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class AgentRunConversationLockTests
{
    [Fact]
    public async Task ExecuteAsync_LockAvailable_ProceedsAndReleasesLock()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new RecordingEngine();
        var conversationLock = new InMemoryConversationLock();
        var run = AgentRunTestFactory.CreateRun(
            engine, store, AgentRunTestFactory.CreateConfig(), conversationLock: conversationLock);

        var result = await run.RunAsync(
            "hello", AgentRunTestFactory.CreateContext("conv-lock-ok"), CancellationToken.None);

        Assert.Equal("final-answer", result);

        var record = await store.GetRecordAsync("tenant-1", "conv-lock-ok", CancellationToken.None);
        Assert.NotNull(record);
        Assert.NotEmpty(record!.Messages);

        var reacquired = await conversationLock.TryAcquireAsync(
            "tenant-1", "conv-lock-ok", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_LockUnavailable_ThrowsConflict()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new RecordingEngine();
        var run = AgentRunTestFactory.CreateRun(
            engine, store, AgentRunTestFactory.CreateConfig(), conversationLock: new NullConversationLock());

        var exception = await Assert.ThrowsAsync<AgentException>(() =>
            run.RunAsync(
                "hello", AgentRunTestFactory.CreateContext("conv-lock-conflict"), CancellationToken.None));

        Assert.Equal(AgentErrorCode.Conflict, exception.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_OnException_ReleasesLock()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new ThrowingEngine();
        var conversationLock = new InMemoryConversationLock();
        var run = AgentRunTestFactory.CreateRun(
            engine, store, AgentRunTestFactory.CreateConfig(), conversationLock: conversationLock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.RunAsync(
                "hello", AgentRunTestFactory.CreateContext("conv-lock-throw"), CancellationToken.None));

        var reacquired = await conversationLock.TryAcquireAsync(
            "tenant-1", "conv-lock-throw", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_NoConversationId_DoesNotAcquireLock()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new RecordingEngine();
        var run = AgentRunTestFactory.CreateRun(
            engine, store, AgentRunTestFactory.CreateConfig(),
            conversationLock: new ThrowingIfAcquiredConversationLock());

        var context = new Dictionary<string, object>
        {
            ["UserId"] = "user-1",
            ["TenantId"] = "tenant-1"
        };

        var result = await run.RunAsync("hello", context, CancellationToken.None);

        Assert.Equal("final-answer", result);
    }

    [Fact]
    public async Task ExecuteStreamAsync_LockAvailable_ProceedsAndReleasesLock()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new SimpleStreamingEngine();
        var conversationLock = new InMemoryConversationLock();
        var run = AgentRunTestFactory.CreateRun(
            engine, store, AgentRunTestFactory.CreateConfig(), conversationLock: conversationLock);

        var chunks = new List<string>();
        await foreach (var chunk in run.RunStreamingAsync(
            "hello", AgentRunTestFactory.CreateContext("conv-lock-stream"), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Contains("final-answer", chunks);

        var reacquired = await conversationLock.TryAcquireAsync(
            "tenant-1", "conv-lock-stream", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    private sealed class NullConversationLock : IConversationLock
    {
        public Task<IConversationLockHandle?> TryAcquireAsync(
            string tenantId, string conversationId, TimeSpan ttl, CancellationToken ct = default)
            => Task.FromResult<IConversationLockHandle?>(null);
    }

    private sealed class ThrowingIfAcquiredConversationLock : IConversationLock
    {
        public Task<IConversationLockHandle?> TryAcquireAsync(
            string tenantId, string conversationId, TimeSpan ttl, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "Conversation lock should not be acquired when conversation context is invalid.");
    }

    private sealed class ThrowingEngine : ITestModelRuntime
    {
        public Task<EngineChatCompletionResult> ChatCompletionAsync(
            EngineChatRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("engine failed");

        public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
            EngineChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

    }

    private sealed class SimpleStreamingEngine : ITestModelRuntime
    {
        public Task<EngineChatCompletionResult> ChatCompletionAsync(
            EngineChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new EngineChatCompletionResult { Content = "final-answer" });

        public async IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
            EngineChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new EngineChatCompletionChunk { Content = "final-answer" };
        }

    }
}
