namespace OpenAgent.Runtime.Acp.Processes;

/// <summary>The terminal record of an owned agent process, published on <see cref="AcpProcessLease.Completion"/>.</summary>
public sealed record AcpProcessExit
{
    /// <summary>
    /// The child's exit code, or null when the platform never reported one (an earlier
    /// platform failure while reaping). A negative value on Unix means a signal death.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>True when the graceful stdin-EOF window expired and the lease killed the tree.</summary>
    public bool Killed { get; init; }

    /// <summary>Cleanup or reap failures collected during the shutdown stages, if any.</summary>
    public Exception? Failure { get; init; }
}
