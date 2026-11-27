using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Observability;
using OpenAgent.Engine.Host.Streaming;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ConversationRunRegistryTests
{
    [Fact]
    public async Task SubscriberCancellation_DoesNotCancelProducerOrOtherSubscribers()
    {
        var allowSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("cancel-isolation"), CreateUser("tenant-1"));
            using var cancelled = new CancellationTokenSource();
            await using IAsyncEnumerator<AgentStreamEvent> first =
                run.ReadAsync(cancelled.Token).GetAsyncEnumerator();
            await using IAsyncEnumerator<AgentStreamEvent> second =
                run.ReadAsync().GetAsyncEnumerator();

            Assert.True(await first.MoveNextAsync());
            Assert.True(await second.MoveNextAsync());
            Assert.Equal("first", first.Current.Content);
            Assert.Equal("first", second.Current.Content);

            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => first.MoveNextAsync().AsTask());

            allowSecond.TrySetResult(true);
            Assert.True(await second.MoveNextAsync());
            Assert.Equal("second", second.Current.Content);
            Assert.False(await second.MoveNextAsync());
            await run.Completion;

        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Content("first");
            await allowSecond.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return Content("second");
        }
    }

    [Fact]
    public async Task ReadAsync_ReplaysPrefixThenStreamsLiveEventsInExactOrder()
    {
        var reachedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("replay-order"), CreateUser("tenant-1"));
            await reachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

            List<string?> contents = [];
            await using (IAsyncEnumerator<AgentStreamEvent> reader = run.ReadAsync().GetAsyncEnumerator())
            {
                Assert.True(await reader.MoveNextAsync());
                contents.Add(reader.Current.Content);
                Assert.True(await reader.MoveNextAsync());
                contents.Add(reader.Current.Content);

                release.TrySetResult(true);
                Assert.True(await reader.MoveNextAsync());
                contents.Add(reader.Current.Content);
                Assert.False(await reader.MoveNextAsync());
            }

            Assert.Equal(["one", "two", "three"], contents);
            await run.Completion;

        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Content("one");
            yield return Content("two");
            reachedGate.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return Content("three");
        }
    }

    [Fact]
    public async Task ReadAsync_PropagatesOriginalProducerException_AndCompletionRemainsSuccessful()
    {
        var failure = new InvalidOperationException("original failure");
        var registry = new ConversationRunRegistry(Producer);
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("error-propagation"), CreateUser("tenant-1"));
            await using IAsyncEnumerator<AgentStreamEvent> reader = run.ReadAsync().GetAsyncEnumerator();

            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("before-failure", reader.Current.Content);
            InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => reader.MoveNextAsync().AsTask());
            Assert.Same(failure, observed);

            await run.Completion;
            Assert.True(run.Completion.IsCompletedSuccessfully);
            Assert.Null(registry.Find("tenant-1", "error-propagation"));

        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Content("before-failure");
            await Task.Yield();
            throw failure;
        }
    }

    [Fact]
    public async Task ExplicitCancel_RemovesRunOnlyAfterProducerFinalization()
    {
        var enteredFinalization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("cancel-join"), CreateUser("tenant-1"));
            run.Cancel();

            await enteredFinalization.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.Completion.IsCompleted);
            Assert.Same(run, registry.Find("tenant-1", "cancel-join"));

            releaseFinalization.TrySetResult(true);
            await run.Completion;
            Assert.Null(registry.Find("tenant-1", "cancel-join"));

        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                enteredFinalization.TrySetResult(true);
                await releaseFinalization.Task.ConfigureAwait(false);
            }
            yield break;
        }
    }

    [Fact]
    public async Task Shutdown_CancelsAndJoinsRunsBeforeTheyAreRemoved()
    {
        var enteredFinalization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        ConversationRun run = registry.Start(CreateRequest("shutdown-join"), CreateUser("tenant-1"));
        Task stop = registry.StopAsync(CancellationToken.None);

        await enteredFinalization.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        Assert.Same(run, registry.Find("tenant-1", "shutdown-join"));

        releaseFinalization.TrySetResult(true);
        await stop;
        Assert.Null(registry.Find("tenant-1", "shutdown-join"));
        await registry.DisposeAsync();

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                enteredFinalization.TrySetResult(true);
                await releaseFinalization.Task.ConfigureAwait(false);
            }
            yield break;
        }
    }

    [Fact]
    public async Task Start_SeparatesTenants_AndRejectsConcurrentSameTenantConversation()
    {
        var registry = new ConversationRunRegistry(HoldUntilCancelled);
        await using (registry)
        {
            ConversationRun first = registry.Start(CreateRequest("same-conversation"), CreateUser("tenant-1"));
            ConversationRun second = registry.Start(CreateRequest("same-conversation"), CreateUser("tenant-2"));

            AgentException conflict = Assert.Throws<AgentException>(
                () => registry.Start(CreateRequest("same-conversation"), CreateUser("tenant-1")));
            Assert.Equal(AgentErrorCode.Conflict, conflict.ErrorCode);

            first.Cancel();
            second.Cancel();
            await Task.WhenAll(first.Completion, second.Completion);
        }

        static async IAsyncEnumerable<AgentStreamEvent> HoldUntilCancelled(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    [Fact]
    public async Task Start_SuppressesAmbientExecutionContext_AndClonesRequestAndUser()
    {
        var ambient = new AsyncLocal<string?>();
        ambient.Value = "http-request";
        var observedAmbient = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        var externalContext = new Dictionary<string, string> { ["key"] = "original" };
        var groups = new[] { "group" };
        var claims = new Dictionary<string, string> { ["claim"] = "original" };
        AgentUserContext user = new()
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            Groups = groups,
            Claims = claims
        };
        AgentRequest request = new()
        {
            Query = "query",
            ConversationId = "ambient-context",
            ExternalContext = externalContext
        };

        await using (registry)
        {
            ConversationRun run = registry.Start(request, user);
            externalContext["key"] = "changed";
            claims["claim"] = "changed";
            groups[0] = "changed";

            await run.Completion;
            Assert.Null(await observedAmbient.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("original", run.Request.ExternalContext!["key"]);
            Assert.Equal("original", run.User.Claims["claim"]);
            Assert.Equal("group", run.User.Groups[0]);
        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest producerRequest,
            IAgentUserContext producerUser,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            observedAmbient.TrySetResult(ambient.Value);
            yield return Content("done");
        }
    }

    [Fact]
    public async Task ProducerActivity_IsExportedWithParentAndCarriesAuditIdentity()
    {
        // 回归：生产者 span 必须经 ActivitySource 创建（可被 OTLP 订阅导出）并挂在
        // HTTP 请求 activity 之下；轮次审计标签写入该 span，审计网关才能关联会话。
        // 旧实现用裸 new Activity()，监听器看不到、永不导出，会话身份全部丢失。
        Activity? exported = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "OpenAgent.Engine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => exported = activity,
        };
        ActivitySource.AddActivityListener(listener);

        Activity http = new Activity("POST /api/v1/agent/chat/stream");
        http.Start();
        try
        {
            var registry = new ConversationRunRegistry(AuditedProducer);
            await using (registry)
            {
                ConversationRun run = registry.Start(CreateRequest("audit-export"), CreateUser("tenant-1"));
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.NotNull(exported);
            Assert.Equal("OpenAgent.ConversationRun", exported!.OperationName);
            Assert.Equal(http.SpanId, exported.ParentSpanId);
            Assert.Equal("audit-export", exported.GetTagItem("openagent.conversation_id"));
            Assert.Equal("user-tenant-1", exported.GetTagItem("openagent.user_id"));
        }
        finally
        {
            http.Stop();
        }

        static async IAsyncEnumerable<AgentStreamEvent> AuditedProducer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            AuditActivityEnrichment.Publish(new TurnContext
            {
                TenantId = user.TenantId ?? string.Empty,
                UserId = user.UserId,
                ConversationId = request.ConversationId,
                TraceId = request.TraceId ?? "trace",
            });
            yield return Content("done");
        }
    }

    [Fact]
    public async Task ReadAsync_AfterExplicitCancel_EndsAsCancelledWithoutFailure()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("cancel-status"), CreateUser("tenant-1"));
            await using IAsyncEnumerator<AgentStreamEvent> reader = run.ReadAsync().GetAsyncEnumerator();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            run.Cancel();

            // 仍在监听的客户端看到的是一次正常的收尾，不是执行失败。
            Assert.False(await reader.MoveNextAsync());
            Assert.Equal("Cancelled", run.CompletionStatus);
            await run.Completion;
        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    [Fact]
    public async Task ReadAsync_LateSubscriberAfterCancel_ReplaysProducedEventsWithoutFailure()
    {
        // 同会话新轮次取代旧轮次后，旧页面重连：只看到已产出的部分，然后正常结束。
        var produced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer);
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("cancel-replay"), CreateUser("tenant-1"));
            await produced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run.Cancel();
            await run.Completion;

            List<string> replayed = [];
            await foreach (AgentStreamEvent item in run.ReadAsync())
            {
                replayed.Add(item.Content);
            }

            Assert.Equal(["partial"], replayed);
            Assert.Equal("Cancelled", run.CompletionStatus);
        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Content("partial");
            produced.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    [Fact]
    public async Task ReadAsync_LastSubscriberLeavesBeforeStart_CancelsTheRunAfterTheGracePeriod()
    {
        var producerObservedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer) { AbandonGrace = TimeSpan.FromMilliseconds(50) };
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("abandoned"), CreateUser("tenant-1"));
            using var leaving = new CancellationTokenSource();
            await using IAsyncEnumerator<AgentStreamEvent> reader = run.ReadAsync(leaving.Token).GetAsyncEnumerator();
            leaving.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());

            // 没有人会再等这一轮：宽限期过后取消，把准入名额、会话锁和进程让出来。
            Assert.True(await producerObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Cancelled", run.CompletionStatus);
            await run.Completion;
        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                producerObservedCancellation.TrySetResult(cancellationToken.IsCancellationRequested);
            }
            yield break;
        }
    }

    [Fact]
    public async Task ReadAsync_SubscriberReattachesWithinGrace_RunIsNotCancelled()
    {
        // 刷新页面：旧连接断开后很快接上新的，不能因此丢掉这一轮。
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer) { AbandonGrace = TimeSpan.FromMilliseconds(50) };
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("reattach"), CreateUser("tenant-1"));
            using var leaving = new CancellationTokenSource();
            await using (IAsyncEnumerator<AgentStreamEvent> first = run.ReadAsync(leaving.Token).GetAsyncEnumerator())
            {
                leaving.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.MoveNextAsync().AsTask());
            }
            await using IAsyncEnumerator<AgentStreamEvent> second = run.ReadAsync().GetAsyncEnumerator();
            // 迭代器到第一次 MoveNextAsync 才真正订阅；真实链路里写入器收到事件流后立即开始读。
            ValueTask<bool> waiting = second.MoveNextAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Null(run.CompletionStatus);

            release.TrySetResult(true);
            Assert.True(await waiting);
            Assert.Equal("answer", second.Current.Content);
            Assert.False(await second.MoveNextAsync());
        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return Content("answer");
        }
    }

    [Fact]
    public async Task ReadAsync_LastSubscriberLeavesAfterStart_RunContinuesToCompletion()
    {
        // 已经开始产出的轮次照旧跑完并持久化：断线或刷新不丢结果。
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ConversationRunRegistry(Producer) { AbandonGrace = TimeSpan.FromMilliseconds(50) };
        await using (registry)
        {
            ConversationRun run = registry.Start(CreateRequest("detached-after-start"), CreateUser("tenant-1"));
            using var leaving = new CancellationTokenSource();
            await using (IAsyncEnumerator<AgentStreamEvent> reader = run.ReadAsync(leaving.Token).GetAsyncEnumerator())
            {
                Assert.True(await reader.MoveNextAsync());
                Assert.Equal("first", reader.Current.Content);
                leaving.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Null(run.CompletionStatus);
            release.TrySetResult(true);
            await run.Completion;

            List<string> replayed = [];
            await foreach (AgentStreamEvent item in run.ReadAsync())
            {
                replayed.Add(item.Content);
            }
            Assert.Equal(["first", "done"], replayed);
            Assert.Null(run.CompletionStatus);
        }

        async IAsyncEnumerable<AgentStreamEvent> Producer(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Content("first");
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return Content("done");
        }
    }

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "query",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };

    private static AgentUserContext CreateUser(string tenantId) => new()
    {
        UserId = $"user-{tenantId}",
        TenantId = tenantId,
        IsAuthenticated = true
    };

    private static AgentStreamEvent Content(string content) => new()
    {
        Type = AgentStreamEventType.Content,
        Content = content
    };
}
