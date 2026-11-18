namespace OpenAgent.Contracts.Security;

/// <summary>Initializes trusted identity in an owned execution scope without retaining an HTTP request.</summary>
public interface ICurrentUserContextInitializer
{
    void Initialize(IAgentUserContext user);
}
