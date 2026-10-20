namespace OpenAgent.Runtime.Acp.Protocol;

public sealed record AcpMethodInfo(string Name, string Direction, bool Notification);
public static class AcpMethods
{
    public const int Version = 1;
    public const string SourceCommit = "1cebe1c01837379abf34dd62b71e9c75bf140669";
    public static IReadOnlyList<AcpMethodInfo> All
    {
        get;
    } = Array.AsReadOnly<AcpMethodInfo>([
        new("initialize", "client_to_agent", false),
        new("authenticate", "client_to_agent", false),
        new("session/new", "client_to_agent", false),
        new("session/load", "client_to_agent", false),
        new("session/set_mode", "client_to_agent", false),
        new("session/set_config_option", "client_to_agent", false),
        new("session/prompt", "client_to_agent", false),
        new("session/cancel", "client_to_agent", true),
        new("session/list", "client_to_agent", false),
        new("session/delete", "client_to_agent", false),
        new("session/resume", "client_to_agent", false),
        new("session/close", "client_to_agent", false),
        new("logout", "client_to_agent", false),
        new("session/request_permission", "agent_to_client", false),
        new("session/update", "agent_to_client", true),
        new("fs/write_text_file", "agent_to_client", false),
        new("fs/read_text_file", "agent_to_client", false),
        new("terminal/create", "agent_to_client", false),
        new("terminal/output", "agent_to_client", false),
        new("terminal/release", "agent_to_client", false),
        new("terminal/wait_for_exit", "agent_to_client", false),
        new("terminal/kill", "agent_to_client", false),
        new("elicitation/create", "agent_to_client", false),
        new("elicitation/complete", "agent_to_client", true),
        new("$/cancel_request", "bidirectional", true),
    ]);
}
