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
    public void UpdateNodes_SameSetInDifferentOrder_PreservesAssignments()
    {
        var first = new JumpHashConsistentHashRing();
        var second = new JumpHashConsistentHashRing();
        first.UpdateNodes(["node-c", "node-a", "node-b"]);
        second.UpdateNodes(["node-b", "node-c", "node-a"]);

        string?[] firstAssignments = Enumerable.Range(0, 100)
            .Select(index => first.GetNode($"conversation-{index}"))
            .ToArray();
        string?[] secondAssignments = Enumerable.Range(0, 100)
            .Select(index => second.GetNode($"conversation-{index}"))
            .ToArray();

        Assert.Equal(firstAssignments, secondAssignments);
    }

    [Fact]
    public void UpdateNodes_DuplicateAndBlankIds_AreIgnored()
    {
        var ring = new JumpHashConsistentHashRing();
        ring.UpdateNodes(["node-a", string.Empty, "node-a", "  "]);

        string?[] assignments = Enumerable.Range(0, 20)
            .Select(index => ring.GetNode($"conversation-{index}"))
            .ToArray();

        Assert.All(assignments, assignment => Assert.Equal("node-a", assignment));
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
