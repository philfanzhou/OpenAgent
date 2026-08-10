namespace OpenAgent.Router.Models;

public sealed record AgentForwardingTarget(
    string DestinationPrefix,
    Uri RequestUri);
