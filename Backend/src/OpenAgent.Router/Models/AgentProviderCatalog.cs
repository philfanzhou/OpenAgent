using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Router.Models;

public sealed record AgentProviderCatalog(
    IReadOnlyList<AgentSummary> Agents,
    bool IsAvailable = true);
