using OpenAgent.Contracts.Security;

namespace OpenAgent.Router;

public interface IAgentVisibilityService
{
    Task<bool> IsAgentVisibleToUserAsync(string agentId, IAgentUserContext userContext, CancellationToken cancellationToken = default);
    Task<List<string>> GetPublishedAgentIdsAsync(CancellationToken cancellationToken = default);
    Task<string?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken = default);
}
