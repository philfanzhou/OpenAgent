namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// The effective, validated configuration selected for one Agent invocation.
/// </summary>
public sealed class AgentRuntimeProfile
{
    public required string AgentId { get; init; }
    public required AgentConfig Config { get; init; }

    /// <summary>
    /// The effective model configuration. Model profile values are overridden by
    /// The selected LLM profile is resolved before the Agent is created.
    /// </summary>
    public required LlmConfig Model { get; init; }
}
