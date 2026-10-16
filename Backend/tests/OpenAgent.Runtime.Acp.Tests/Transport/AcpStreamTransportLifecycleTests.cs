using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using OpenAgent.Runtime.Acp.Transport;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Transport;

public sealed class AcpStreamTransportLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_ConcurrentCalls_AwaitSameCleanupAndResult(bool fail)
    {
        using ControlledDisposeStream input = new(block: true, fail: fail);
        using ControlledDisposeStream output = new();
        AcpStreamTransport transport = new(input, output);
        Task first = transport.DisposeAsync().AsTask();
        await input.Entered.Task.WaitAsync(Timeout);
        Task second = transport.DisposeAsync().AsTask();
        bool returnedEarly = second.IsCompleted;
        bool completedEarly = transport.Completion.IsCompleted;
        input.Release.TrySetResult();

        Exception? firstFailure = await Record.ExceptionAsync(() => first.WaitAsync(Timeout));
        Exception? secondFailure = await Record.ExceptionAsync(() => second.WaitAsync(Timeout));
        Exception? completionFailure = await Record.ExceptionAsync(() => transport.Completion.WaitAsync(Timeout));
        Assert.False(returnedEarly);
        Assert.False(completedEarly);
        Assert.Equal(1, input.DisposeCount);
        Assert.Equal(1, output.DisposeCount);
        Assert.Same(firstFailure, secondFailure);
        Assert.Same(firstFailure, completionFailure);
        if (fail)
        {
            Assert.Same(input.Failure, firstFailure);
        }
        else
        {
            Assert.Null(firstFailure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_ThrowingCancellation_StillCleansOwnedStreamsAndCompletes(bool failOutputDispose)
    {
        using PendingReadStream input = new(throwOnCancel: true);
        using ControlledDisposeStream output = new(fail: failOutputDispose);
        AcpStreamTransport transport = new(input, output);
        Task<byte[]?> read = transport.ReadAsync();
        await input.Entered.Task.WaitAsync(Timeout);

        Exception? failure = await Record.ExceptionAsync(() => transport.DisposeAsync().AsTask().WaitAsync(Timeout));
        AggregateException aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Contains(aggregate.Flatten().InnerExceptions, error => error.Message.Contains("cancellation callback"));
        if (failOutputDispose)
        {
            Assert.Contains(output.Failure, aggregate.Flatten().InnerExceptions);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Timeout));
        Assert.Equal(1, input.DisposeCount);
        Assert.Equal(1, output.DisposeCount);
        Assert.False(input.CanRead);
        Assert.False(output.CanWrite);
        Assert.Same(failure, await Record.ExceptionAsync(() => transport.Completion.WaitAsync(Timeout)));
        Assert.Same(failure, await Record.ExceptionAsync(() => transport.DisposeAsync().AsTask().WaitAsync(Timeout)));
    }

    [Fact]
    public async Task DisposeAsync_BothStreamsThrow_PreservesBothFailures()
    {
        using ControlledDisposeStream input = new(fail: true);
        using ControlledDisposeStream output = new(fail: true);
        AcpStreamTransport transport = new(input, output);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => transport.DisposeAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal(new[] { input.Failure, output.Failure }, failure.InnerExceptions);
        Assert.Same(failure, await Record.ExceptionAsync(() => transport.Completion.WaitAsync(Timeout)));
        Assert.Equal(1, input.DisposeCount);
        Assert.Equal(1, output.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_EofWithInflightWrite_CompletesAfterOutputSettles(bool failWrite)
    {
        using MemoryStream input = new();
        using PendingWriteStream output = new(failOnRelease: failWrite);
        AcpStreamTransport transport = new(input, output, new AcpTransportOptions { LeaveOpen = true });
        Task write = transport.WriteAsync("{}"u8.ToArray());
        await output.Entered.Task.WaitAsync(Timeout);
        Assert.Null(await transport.ReadAsync().WaitAsync(Timeout));
        bool completedEarly = transport.Completion.IsCompleted;
        Task nextWrite = transport.WriteAsync("[]"u8.ToArray());
        output.Release.TrySetResult();

        Exception? writeFailure = await Record.ExceptionAsync(() => write.WaitAsync(Timeout));
        Exception? rejectedWrite = await Record.ExceptionAsync(() => nextWrite.WaitAsync(Timeout));
        Exception? completionFailure = await Record.ExceptionAsync(() => transport.Completion.WaitAsync(Timeout));
        await transport.DisposeAsync();
        Assert.False(completedEarly);
        Assert.IsType<IOException>(rejectedWrite);
        if (failWrite)
        {
            Assert.Same(output.Failure, writeFailure);
            Assert.Same(output.Failure, completionFailure);
        }
        else
        {
            Assert.Null(writeFailure);
            Assert.Null(completionFailure);
            Assert.Equal("{}\n"u8.ToArray(), output.ToArray());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_DuringEofDrain_CancelsActiveAndQueuedOperations(bool leaveOpen)
    {
        using MemoryStream input = new();
        using PendingWriteStream output = new();
        AcpStreamTransport transport = new(input, output, new AcpTransportOptions { LeaveOpen = leaveOpen });
        Task write = transport.WriteAsync("{}"u8.ToArray());
        await output.Entered.Task.WaitAsync(Timeout);
        Task queued = transport.WriteAsync("[]"u8.ToArray());
        Assert.Null(await transport.ReadAsync().WaitAsync(Timeout));

        await transport.DisposeAsync().AsTask().WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Timeout));
        Exception? queuedFailure = await Record.ExceptionAsync(() => queued.WaitAsync(Timeout));
        Assert.True(queuedFailure is OperationCanceledException or ObjectDisposedException);
        await transport.Completion.WaitAsync(Timeout);
        Assert.Equal(leaveOpen, input.CanRead);
        Assert.Equal(leaveOpen, output.CanWrite);
    }
}
