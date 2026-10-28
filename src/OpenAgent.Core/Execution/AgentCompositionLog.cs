using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Execution;

internal static partial class AgentCompositionLog
{
    [LoggerMessage(EventId = 2201, Level = LogLevel.Information,
        Message = "Agent tool definitions: {Count} tools, {Chars} chars (~{Tokens} tokens per request)")]
    internal static partial void ToolDefinitions(ILogger logger, int count, int chars, int tokens);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning,
        Message = "Agent feature cleanup failed during composition.")]
    internal static partial void CleanupFailed(ILogger logger, Exception exception);
}
