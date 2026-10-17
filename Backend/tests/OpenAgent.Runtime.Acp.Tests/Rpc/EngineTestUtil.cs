using System.Buffers;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>Small helpers shared by RPC engine tests.</summary>
internal static class EngineTestUtil
{
    public static JsonElement BuildElement(Action<Utf8JsonWriter> action)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) action(writer);
        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            if (cancellationToken.IsCancellationRequested) throw new XunitException("Condition was not met within the test deadline.");
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Asserts no frame is written within a short grace, for paths that must not answer.</summary>
    public static async Task AssertNoFrameAsync(OpenAgent.Runtime.Acp.Tests.TestDoubles.LoopbackTransport wire, CancellationToken cancellationToken)
    {
        using CancellationTokenSource grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(150);
        byte[]? frame;
        try
        {
            frame = await wire.OutgoingFrames.ReadAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Nothing arrived within the grace: correct.
        }

        if (frame is not null) throw new XunitException("The engine wrote a frame where none was expected.");
    }
}
