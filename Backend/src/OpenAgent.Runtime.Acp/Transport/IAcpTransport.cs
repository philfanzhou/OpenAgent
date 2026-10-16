namespace OpenAgent.Runtime.Acp.Transport;

/// <summary>Transfers complete UTF-8 message bodies without newline delimiters.</summary>
public interface IAcpTransport : IAsyncDisposable
{
    Task Completion { get; }

    Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}
