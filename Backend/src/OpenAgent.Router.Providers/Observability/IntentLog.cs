using Microsoft.Extensions.Logging;

namespace OpenAgent.Router.Observability;

/// <summary>Logging definitions for intent strategies that call external services.</summary>
internal static partial class IntentLog
{
    [LoggerMessage(EventId = 3035, Level = LogLevel.Warning, Message = "Decision model call failed; trying the next strategy. Endpoint={Endpoint}, Reason={Reason}")]
    public static partial void DecisionModelFailed(ILogger logger, Exception? exception, string endpoint, string reason);
}
