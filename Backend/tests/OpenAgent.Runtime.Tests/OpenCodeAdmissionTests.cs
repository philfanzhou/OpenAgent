using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Runtime.Tests;

[Collection(RealOpenCodeCollection.Name)]
public sealed class OpenCodeAdmissionTests
{
    [OpenCodeFact]
    public async Task RunAsync_PhysicalGateTimeoutOrCancellation_DoesNotCreateAnotherWorkspace()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, _) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings =>
        {
            settings["OpenCode:MaxConcurrentTurns"] = "1";
            settings["OpenCode:RequestTimeoutSeconds"] = "3";
        });
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(45));
        Task active = runtime.CollectAsync(runtime.Request, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        RuntimeAdmissionException error = await Assert.ThrowsAsync<RuntimeAdmissionException>(() => runtime.CollectAsync(runtime.Request));
        Assert.Equal(AgentErrorCode.RuntimeQueueTimeout, error.ErrorCode);
        using CancellationTokenSource waiting = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CollectAsync(runtime.Request, waiting.Token));
        Assert.Single(Directory.GetDirectories(runtime.Root, "t-*", SearchOption.AllDirectories));
        Assert.Single(Directory.GetDirectories(runtime.Root, "p-*", SearchOption.AllDirectories));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(15)));
        runtime.AssertWorkspacesReleased();
    }
}
