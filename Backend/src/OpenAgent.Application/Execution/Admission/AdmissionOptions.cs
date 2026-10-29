namespace OpenAgent.Application.Execution.Admission;

/// <summary>Host-local admission limits. Queueing never owns preparation resources.</summary>
public sealed class AdmissionOptions
{
    public const string SectionName = "RuntimeAdmission";
    public int MaxConcurrentTurns { get; set; } = 2;
    public int MaxQueuedTurns { get; set; } = 8;
    public int MaxConcurrentPerTenant { get; set; } = 1;
    public int MaxQueuedPerTenant { get; set; } = 4;
    public int QueueTimeoutSeconds { get; set; } = 30;
    public int ExecutionTimeoutSeconds { get; set; } = 1800;
    public int RetryAfterSeconds { get; set; } = 1;
}
