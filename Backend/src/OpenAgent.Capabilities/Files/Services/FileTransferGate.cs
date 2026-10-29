using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Capabilities.Files.Services;

/// <summary>Host-wide bound on simultaneous buffered transfers, shared by all turn scopes.</summary>
internal sealed class FileTransferGate(IOptions<FileExchangeOptions> options) : IDisposable
{
    internal SemaphoreSlim Transfers { get; } = new(options.Value.MaxConcurrentTransfers);

    public void Dispose() => Transfers.Dispose();
}
