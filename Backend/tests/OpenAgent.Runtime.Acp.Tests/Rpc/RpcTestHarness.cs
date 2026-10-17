using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;

namespace OpenAgent.Runtime.Acp.Tests.Rpc;

/// <summary>Shared harness for engine tests: paired connections, raw frame injection, and bounded taps.</summary>
internal static class RpcTestHarness
{
    public const int TimeoutMs = 10_000;

    public static (AcpRpcConnection Client, AcpRpcConnection Agent, LoopbackTransport ClientWire, LoopbackTransport AgentWire) Connect(
        AcpRpcOptions? clientOptions = null,
        AcpRpcOptions? agentOptions = null)
    {
        (LoopbackTransport clientWire, LoopbackTransport agentWire) = LoopbackTransport.CreatePair();
        AcpRpcConnection client = new(clientWire, AcpRpcRole.Client, clientOptions);
        AcpRpcConnection agent = new(agentWire, AcpRpcRole.Agent, agentOptions);
        return (client, agent, clientWire, agentWire);
    }

    /// <summary>Writes one raw wire frame to the transport, as a misbehaving peer would.</summary>
    public static Task SendFrameAsync(LoopbackTransport wire, string json) =>
        wire.WriteAsync(Encoding.UTF8.GetBytes(json));

    /// <summary>Reads the next frame the side's engine wrote, with a bounded wait; null on timeout.</summary>
    public static async Task<JsonDocument?> ReceiveFrameAsync(LoopbackTransport wire, CancellationToken cancellationToken)
    {
        byte[]? frame = await wire.OutgoingFrames.ReadAsync(cancellationToken).ConfigureAwait(false);
        return frame is null ? null : JsonDocument.Parse(frame);
    }
}
