namespace OpenAgent.Contracts.Configuration;

using OpenAgent.Contracts.Conversation;

/// <summary>
/// The effective, validated configuration selected for one Agent invocation.
/// </summary>
public sealed class AgentRuntimeProfile
{
    public required string AgentId { get; init; }
    public required AgentConfig Config { get; init; }
    public required LlmConfig Model { get; init; }
    public ContextPolicy? ContextPolicy { get; init; }
}
