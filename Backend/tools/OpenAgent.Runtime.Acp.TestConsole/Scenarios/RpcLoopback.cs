using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>An in-memory transport pair for scenarios: one side's writes are the other side's reads, with an output tap.</summary>
internal sealed class RpcLoopback : IAcpTransport
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<byte[]> _tap = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private RpcLoopback _peer = null!;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _fault;

    private RpcLoopback() { }

    public static (RpcLoopback ClientSide, RpcLoopback AgentSide) CreatePair()
    {
        RpcLoopback clientSide = new();
        RpcLoopback agentSide = new();
        clientSide._peer = agentSide;
        agentSide._peer = clientSide;
        return (clientSide, agentSide);
    }

    public Task Completion => _completion.Task;

    /// <summary>Frames this side's engine wrote, in order, for scenario assertions.</summary>
    public ChannelReader<byte[]> OutgoingFrames => _tap.Reader;

    public void SimulateEof()
    {
        _inbound.Writer.TryComplete();
        _completion.TrySetResult();
    }

    public void Fault(Exception exception)
    {
        _fault ??= exception;
        _inbound.Writer.TryComplete();
        _completion.TrySetException(exception);
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return _inbound.Reader.TryRead(out byte[]? frame) ? frame : null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (_fault is not null) throw new IOException("The loopback transport faulted.", _fault);
        if (_inbound.Reader.Completion.IsCompleted) throw new IOException("The loopback transport saw EOF; writes are rejected.");
        byte[] copy = message.ToArray();
        await _tap.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
        await _peer._inbound.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A pair of started engines over a loopback pair, with a bounded test deadline.</summary>
internal sealed class RpcHarness : IAsyncDisposable
{
    internal const int TimeoutMs = 10_000;

    private RpcHarness(
        OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection client,
        OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection agent,
        RpcLoopback clientWire,
        RpcLoopback agentWire,
        CancellationTokenSource deadline)
    {
        Client = client;
        Agent = agent;
        ClientWire = clientWire;
        AgentWire = agentWire;
        Deadline = deadline;
    }

    public OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection Client { get; }
    public OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection Agent { get; }
    public RpcLoopback ClientWire { get; }
    public RpcLoopback AgentWire { get; }
    public CancellationTokenSource Deadline { get; }

    /// <summary>Creates the pair, lets the scenario register handlers, then starts both engines.</summary>
    public static Task<RpcHarness> StartAsync(
        OpenAgent.Runtime.Acp.Rpc.AcpRpcOptions? clientOptions = null,
        OpenAgent.Runtime.Acp.Rpc.AcpRpcOptions? agentOptions = null,
        Action<OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection, OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection>? configure = null)
    {
        (RpcLoopback clientWire, RpcLoopback agentWire) = RpcLoopback.CreatePair();
        OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection client = new(clientWire, OpenAgent.Runtime.Acp.Rpc.AcpRpcRole.Client, clientOptions);
        OpenAgent.Runtime.Acp.Rpc.AcpRpcConnection agent = new(agentWire, OpenAgent.Runtime.Acp.Rpc.AcpRpcRole.Agent, agentOptions);
        RpcHarness harness = new(client, agent, clientWire, agentWire, new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs)));
        configure?.Invoke(client, agent);
        client.Start();
        agent.Start();
        return Task.FromResult(harness);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await Agent.DisposeAsync().ConfigureAwait(false);
        Deadline.Dispose();
    }
}
