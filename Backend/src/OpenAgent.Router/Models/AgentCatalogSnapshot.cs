namespace OpenAgent.Router.Models;

internal sealed record AgentCatalogSnapshot(
    IReadOnlyList<AgentCatalogEntry> Entries);
