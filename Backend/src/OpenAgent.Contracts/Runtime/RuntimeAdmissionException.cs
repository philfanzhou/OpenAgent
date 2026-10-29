using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Runtime;

/// <summary>Stable overload/queue timeout information for HTTP and streaming transports.</summary>
public sealed class RuntimeAdmissionException(AgentErrorCode code, string message, int retryAfterSeconds)
    : AgentException(code, message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
