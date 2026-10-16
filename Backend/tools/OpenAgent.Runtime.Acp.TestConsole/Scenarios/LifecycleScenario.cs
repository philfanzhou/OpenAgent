using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

internal static class LifecycleScenario
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        using ProbeStream output = new() { FaultWrite = true };
        await using (AcpStreamTransport transport = new(new MemoryStream(), output, new AcpTransportOptions { LeaveOpen = true }))
        {
            Task write = transport.WriteAsync("{}"u8.ToArray());
            bool completedEarly;
            try
            {
                await output.Entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
                if (await transport.ReadAsync().ConfigureAwait(false) is not null)
                {
                    throw new InvalidDataException("Expected EOF.");
                }

                completedEarly = transport.Completion.IsCompleted;
            }
            finally
            {
                output.Release.TrySetResult();
            }

            Exception? writeFailure = await ObserveAsync(write).ConfigureAwait(false);
            Exception? completionFailure = await ObserveAsync(transport.Completion).ConfigureAwait(false);
            if (completedEarly || writeFailure is not IOException || !ReferenceEquals(writeFailure, completionFailure))
            {
                throw new InvalidDataException("EOF masked the output failure.");
            }
        }

        using ProbeStream input = new() { ThrowOnCancel = true };
        using MemoryStream ownedOutput = new();
        AcpStreamTransport cancellation = new(input, ownedOutput);
        Task read = cancellation.ReadAsync();
        await input.Entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Exception? cleanupFailure = await ObserveAsync(cancellation.DisposeAsync().AsTask()).ConfigureAwait(false);
        Exception? readFailure = await ObserveAsync(read).ConfigureAwait(false);
        Exception? terminalFailure = await ObserveAsync(cancellation.Completion).ConfigureAwait(false);
        Exception? repeatedFailure = await ObserveAsync(cancellation.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (cleanupFailure is not AggregateException || readFailure is not OperationCanceledException ||
            !ReferenceEquals(cleanupFailure, terminalFailure) || !ReferenceEquals(cleanupFailure, repeatedFailure) ||
            input.CanRead || ownedOutput.CanWrite)
        {
            throw new InvalidDataException("Cancellation failure skipped cleanup or lost its result.");
        }

        using ProbeStream slowInput = new() { GateDispose = true };
        using MemoryStream finalOutput = new();
        AcpStreamTransport concurrent = new(slowInput, finalOutput);
        Task first = concurrent.DisposeAsync().AsTask();
        bool returnedEarly;
        Task second;
        try
        {
            await slowInput.Entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
            second = concurrent.DisposeAsync().AsTask();
            returnedEarly = second.IsCompleted;
        }
        finally
        {
            slowInput.Release.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(Timeout).ConfigureAwait(false);
        await concurrent.Completion.ConfigureAwait(false);
        if (returnedEarly || finalOutput.CanWrite)
        {
            throw new InvalidDataException("Concurrent disposal completed before cleanup.");
        }
    }

    private static async Task<Exception?> ObserveAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Timeout).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
