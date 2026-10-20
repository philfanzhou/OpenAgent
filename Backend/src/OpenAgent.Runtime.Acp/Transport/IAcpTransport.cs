namespace OpenAgent.Runtime.Acp.Transport;

/// <summary>Transfers complete UTF-8 message bodies without newline delimiters.</summary>
public interface IAcpTransport : IAsyncDisposable
{
    /// <summary>Completes after terminal input, in-flight operations and owned resource cleanup settle.</summary>
    /// <remarks>EOF rejects new operations and drains an in-flight write; disposal or a fault may interrupt it.
    /// Transport and cleanup failures fault this task. Concurrent disposal awaits the same cleanup result.</remarks>
    Task Completion
    {
        get;
    }

    Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}
