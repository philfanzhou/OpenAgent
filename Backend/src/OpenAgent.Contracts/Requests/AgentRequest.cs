using System.Text.Json.Serialization;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Contracts.Requests;

public class AgentRequest
{
    public required string Query { get; init; }
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
    public ConversationType ConversationType { get; init; } = ConversationType.User;
    public string? TraceId { get; init; }
    public ClientType ClientType { get; init; } = ClientType.Web;
    public string? IdempotencyKey { get; init; }
    public Dictionary<string, string>? ExternalContext { get; init; }
    public int? ContextWindowTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    [JsonIgnore]
    public IReadOnlyList<string> FileIds { get; init; } = Array.Empty<string>();
}

public enum ClientType
{
    Web = 0,
    Mobile = 1,
    Desktop = 2,
    API = 3,
    /// <summary>Microsoft Teams 渠道</summary>
    Teams = 4,
    /// <summary>Microsoft Outlook 邮件渠道</summary>
    Outlook = 5,
    /// <summary>系统触发（Cron/Probe）</summary>
    System = 6
}
