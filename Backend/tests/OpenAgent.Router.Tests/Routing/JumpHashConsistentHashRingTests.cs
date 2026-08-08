using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class JumpHashConsistentHashRingTests
{
    [Fact]
    public void GetNode_WithoutNodes_ReturnsNull()
    {
        var ring = new JumpHashConsistentHashRing();

        Assert.Null(ring.GetNode("any-key"));
    }

    [Fact]
    public void GetNode_WithNodes_ReturnsAssignedNode()
    {
        var ring = new JumpHashConsistentHashRing();
        string[] nodes = ["node-a", "node-b", "node-c"];
        ring.UpdateNodes(nodes);

        string? node = ring.GetNode("my-key");

        Assert.NotNull(node);
        Assert.Contains(node, nodes);
    }

    [Fact]
    public void GetNode_SameKey_IsDeterministic()
    {
        var ring = new JumpHashConsistentHashRing();
        ring.UpdateNodes(["n1", "n2", "n3", "n4"]);

        string? first = ring.GetNode("stable-key");

        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(first, ring.GetNode("stable-key"));
        }
    }

    [Fact]
    public void UpdateNodes_EmptyCollection_MakesGetNodeReturnNull()
    {
        var ring = new JumpHashConsistentHashRing();
        ring.UpdateNodes(["node-a"]);
        Assert.NotNull(ring.GetNode("key"));

        ring.UpdateNodes([]);

        Assert.Null(ring.GetNode("key"));
    }

    [Fact]
    public void UpdateNodes_Null_MakesGetNodeReturnNull()
    {
        var ring = new JumpHashConsistentHashRing();

        ring.UpdateNodes(null!);

        Assert.Null(ring.GetNode("key"));
    }

    [Fact]
    public void GetNode_KeysAreDistributedAcrossNodes()
    {
        var ring = new JumpHashConsistentHashRing();
        ring.UpdateNodes(["n1", "n2", "n3"]);

        List<string?> assignedNodes = Enumerable.Range(0, 300)
            .Select(index => ring.GetNode($"key-{index}"))
            .Distinct()
            .ToList();

        Assert.Equal(3, assignedNodes.Count);
    }
}
