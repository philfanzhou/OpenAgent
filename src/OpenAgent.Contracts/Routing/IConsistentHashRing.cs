namespace OpenAgent.Contracts.Routing;

/// <summary>
/// Consistent hash ring for mapping keys to nodes with minimal redistribution on membership changes.
/// </summary>
public interface IConsistentHashRing
{
    /// <summary>
    /// Get the node id for the given key. Returns null if the ring is empty.
    /// </summary>
    string? GetNode(string key);

    /// <summary>
    /// Update the set of nodes on the ring.
    /// </summary>
    void UpdateNodes(IEnumerable<string> nodeIds);
}
