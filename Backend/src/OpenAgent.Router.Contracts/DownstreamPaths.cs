namespace OpenAgent.Router;

/// <summary>
/// Fixed paths every downstream serves, as listed in openapi/downstream-v1.yaml.
/// Downstreams do not configure them; Router does not translate other protocols.
/// </summary>
public static class DownstreamPaths
{
    public const string AgentList = "/api/v1/agent/agents";
    public const string Chat = "/api/v1/agent/chat";
    public const string ChatIntent = "/api/v1/agent/chat/intent";
    public const string ConversationProbe = "/api/v1/agent/provider/conversations";
}
