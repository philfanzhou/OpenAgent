using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Router.Models;

internal sealed record AgentCatalogEntry(
    AgentSummary Agent,
    string ProviderId);
