using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Models;

/// <summary>
/// Entity representing the complete configuration state of an Agent in the Matrix.
/// </summary>
public class AgentConfigEntity
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public AgentPublishStatus Status { get; set; } = AgentPublishStatus.Draft;

    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    /// The runtime configuration representation.
    /// </summary>
    public AgentConfig Config { get; set; } = new();
}

public enum AgentPublishStatus
{
    Draft,
    PendingReview,
    Snapshot
}
