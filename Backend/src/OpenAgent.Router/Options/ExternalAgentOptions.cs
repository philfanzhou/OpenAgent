namespace OpenAgent.Router.Options;

internal sealed class ExternalAgentRoutingOptions
{
    internal const string SectionName = "RouterSettings:ExternalAgents";

    public List<ExternalAgentOptions> Agents { get; set; } = [];
}

internal sealed class ExternalAgentOptions
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Adapter { get; set; } = "OpenAgent";
    public string BaseUrl { get; set; } = string.Empty;
    public string ChatPath { get; set; } = "/api/v1/agent/chat";
    public string? RemoteAgentId { get; set; }
    public bool ForwardIdentityHeaders { get; set; }
    public bool ForwardGatewayGrant { get; set; }
    public string? GatewayAudience { get; set; }
    public ExternalAgentAuthenticationOptions Authentication { get; set; } = new();
}

internal sealed class ExternalAgentAuthenticationOptions
{
    public string HeaderName { get; set; } = "Authorization";
    public string Scheme { get; set; } = "Bearer";
    public string Token { get; set; } = string.Empty;
}
