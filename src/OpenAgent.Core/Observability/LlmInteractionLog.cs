using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Observability;

internal static partial class LlmInteractionLog
{
    [LoggerMessage(
        EventId = 1460,
        Level = LogLevel.Information,
        Message = "LLM interaction recorded. ConversationId={ConversationId} TraceId={TraceId} AgentId={AgentId} Model={ModelId} Source={Source} Status={Status} DurationMs={DurationMs} CallIndex={CallIndex}")]
    internal static partial void Recorded(
        ILogger logger,
        string? conversationId,
        string traceId,
        string? agentId,
        string modelId,
        string source,
        string status,
        int durationMs,
        int callIndex);

    [LoggerMessage(
        EventId = 1461,
        Level = LogLevel.Warning,
        Message = "LLM interaction log write failed. ConversationId={ConversationId} TraceId={TraceId}")]
    internal static partial void RecordFailed(
        ILogger logger,
        string? conversationId,
        string traceId,
        Exception? exception);
}
