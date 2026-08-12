namespace OpenAgent.Contracts.Infrastructure;

/// <summary>
/// Selects the infrastructure implementation used for durable application data.
/// Contracts and Core remain independent from a particular database provider.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Provider name. PostgreSql is the current implementation; additional
    /// providers can be added in Infrastructure without changing Core contracts.
    /// </summary>
    public string Provider { get; set; } = "PostgreSql";

    /// <summary>
    /// Name of the configured connection string rather than a provider-specific
    /// connection string property.
    /// </summary>
    public string ConnectionStringName { get; set; } = "OpenAgentDatabase";
}
