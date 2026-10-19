using System.Text.Json;

namespace OpenAgent.Runtime.Acp.Protocol;

public sealed record PermissionOption(string OptionId, string Name, string Kind) : AcpData;
public sealed record RequestPermissionRequest(string SessionId, ToolCallUpdate ToolCall, PermissionOption[] Options) : AcpData;
public sealed record PermissionOutcome(string Outcome, string? OptionId = null) : AcpData;
public sealed record PermissionResponse(PermissionOutcome Outcome) : AcpData
{
    public static PermissionResponse Cancelled { get; } = new(new PermissionOutcome("cancelled"));
    public static PermissionResponse Selected(string optionId) => new(new PermissionOutcome("selected", optionId));
}
public sealed record ReadFileRequest(string SessionId, string Path, uint? Line = null, uint? Limit = null) : AcpData;
public sealed record ReadFileResponse(string Content) : AcpData;
public sealed record WriteFileRequest(string SessionId, string Path, string Content) : AcpData;
public sealed record CreateTerminalRequest(string SessionId, string Command, string[]? Args = null, EnvVariable[]? Env = null, string? Cwd = null, ulong? OutputByteLimit = null) : AcpData;
public sealed record CreateTerminalResponse(string TerminalId) : AcpData;
public sealed record TerminalRequest(string SessionId, string TerminalId) : AcpData;
public sealed record ExitStatus(int? ExitCode = null, string? Signal = null) : AcpData;
public sealed record TerminalOutputResponse(string Output, bool Truncated, ExitStatus? ExitStatus = null) : AcpData;
public sealed record ElicitationRequest(string Message, string Mode) : AcpData
{
    public string? SessionId
    {
        get; init;
    }
    public RpcId? RequestId
    {
        get; init;
    }
    public string? ToolCallId
    {
        get; init;
    }
    public JsonElement? RequestedSchema
    {
        get; init;
    }
    public string? Url
    {
        get; init;
    }
    public string? ElicitationId
    {
        get; init;
    }
}
public sealed record ElicitationResponse(string Action, JsonElement? Content = null) : AcpData;
public sealed record ElicitationComplete(string ElicitationId) : AcpData;
public sealed record CancelRequest(RpcId RequestId);
