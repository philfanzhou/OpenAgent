namespace OpenAgent.Router;

/// <summary>Body of <c>PUT /api/v1/router/registrations/{instanceId}</c>; sent again on every renewal.</summary>
public sealed class RegistrationRequest
{
    public const string KeyHeader = "X-Registration-Key";

    public string BaseUrl { get; set; } = string.Empty;
    public int TtlSeconds { get; set; } = 30;
    public int Load { get; set; }
    public string[] Intents { get; set; } = [];

    public static string Route(string instanceId) => $"/api/v1/router/registrations/{instanceId}";
}
