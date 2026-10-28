namespace OpenAgent.Router.Options;

/// <summary>
/// Configuration for internal service-to-service trust (e.g. Channels → Router without JWT).
/// </summary>
public sealed class InternalServiceOptions
{
    /// <summary>
    /// Shared secret that an internal service must present via the <c>X-Internal-Token</c> header.
    /// Must be non-empty to enable internal service authentication.
    /// </summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>
    /// Allow-list of internal services keyed by service name. The service identified by the
    /// <c>X-Internal-Service</c> header must be present in this dictionary (with a non-empty value)
    /// to be trusted.
    /// </summary>
    public Dictionary<string, string> AllowedServices { get; set; } = new();
}
