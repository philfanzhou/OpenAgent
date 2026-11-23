namespace OpenAgent.Contracts.Files;

/// <summary>Limits on transferring assets into and out of runtime workspaces, not an OS disk quota.</summary>
public sealed class FileExchangeOptions
{
    public const string SectionName = "FileExchange";
    public bool Enabled { get; init; }
    public int MaxInputFiles { get; init; } = 10;
    public long MaxInputBytes { get; init; } = 200 * 1024 * 1024;
    public int MaxOutputFiles { get; init; } = 10;
    public long MaxOutputBytes { get; init; } = 200 * 1024 * 1024;
    public int MaxConcurrentTransfers { get; init; } = 2;
}
