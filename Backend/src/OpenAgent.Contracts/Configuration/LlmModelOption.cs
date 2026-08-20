namespace OpenAgent.Contracts.Configuration;

public sealed class LlmModelOption
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
}
