using System.Runtime.CompilerServices;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
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
