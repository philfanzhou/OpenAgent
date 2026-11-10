namespace OpenAgent.Router.Options;

/// <summary>
/// Settings for the Intern-Decision-4B routing service.
/// </summary>
/// <remarks>
/// The decision service is stateless by design: it stores no agents and no
/// sessions. Every request must therefore carry the caller's own candidate
/// list, which is why the list is filtered by <c>IAgentAccessControl</c> and
/// passed along rather than configured here.
/// </remarks>
internal sealed class InternDecisionOptions
{
    /// <summary>Registry name of the provider type.</summary>
    internal const string ProviderType = "InternDecision";

    /// <summary>
    /// Environment variable holding the API key.
    /// </summary>
    /// <remarks>
    /// The key deliberately does not come from appsettings: that file is
    /// committed, and a shared secret must not be. Reading it from the
    /// environment matches how the platform handles every other credential.
    /// </remarks>
    internal const string ApiKeyEnvironmentVariable = "OPENAGENT_INTERN_DECISION_API_KEY";

    /// <summary>Base address of the decision service, for example <c>http://10.51.1.106:8000</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Path of the routing endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string RoutePath { get; set; } = "/v1/route";

    /// <summary>Path of the health endpoint, used to report availability.</summary>
    public string HealthPath { get; set; } = "/health";

    /// <summary>Per-request timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Agent id used as the model's fallback option.
    /// </summary>
    /// <remarks>
    /// The service requires a fallback inside the candidate list: the model only
    /// picks from what it is given, so without an escape hatch it is forced to
    /// choose something arbitrary. Resolved from the intent-recognition
    /// fallback when left empty.
    /// </remarks>
    public string FallbackAgentId { get; set; } = "default";

    /// <summary>Description stored on the fallback entry when the agent has none.</summary>
    public string FallbackDescription { get; set; } = "无法明确归入以上任何类别的用户请求";

    /// <summary>
    /// Description used for candidate agents that carry none of their own.
    /// </summary>
    /// <remarks>
    /// The service rejects blank descriptions, so a placeholder keeps a
    /// half-configured agent from breaking routing for every user.
    /// </remarks>
    public string DefaultAgentDescription { get; set; } = "通用对话助手";

    /// <summary>Whether an API key was found in the environment.</summary>
    internal bool HasApiKey => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <summary>Reads the API key from the environment, or <see langword="null"/>.</summary>
    internal static string? ResolveApiKey() =>
        NullIfBlank(Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable));

    /// <summary>Whether the settings describe a usable service.</summary>
    internal bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(RoutePath)
        && TimeoutMs > 0;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}