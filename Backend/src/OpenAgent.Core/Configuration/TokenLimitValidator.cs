using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Configuration;

public static class TokenLimitValidator
{
    public static void ValidateConfiguration(
        int? contextWindowTokens,
        int? maxOutputTokens,
        AgentErrorCode errorCode = AgentErrorCode.ConfigurationError)
    {
        if (contextWindowTokens is <= 0 || maxOutputTokens is <= 0)
        {
            throw new AgentException(errorCode, "Token limits must be positive integers.");
        }
        if (contextWindowTokens.HasValue && maxOutputTokens >= contextWindowTokens)
        {
            throw new AgentException(errorCode,
                "Maximum output tokens must be less than the context window.");
        }
    }
}
