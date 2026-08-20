namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Identifies a selectable model without carrying provider credentials.
/// </summary>
public sealed class LlmModelSelection
{
    public string Provider { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
}
