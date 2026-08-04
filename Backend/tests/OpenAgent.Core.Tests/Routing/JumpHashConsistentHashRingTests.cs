using OpenAgent.Core.Routing;
using Xunit;

namespace OpenAgent.Core.Tests.Routing;

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
        ring.UpdateNodes(new[] { "node-a", "node-b", "node-c" });

        var node = ring.GetNode("my-key");
        Assert.NotNull(node);
        Assert.Contains(node, new[] { "node-a", "node-b", "node-c" });
    }

    [Fact]
    public void GetNode_SameKey_IsDeterministic()
    {
        var ring = new JumpHashConsistentHashRing();
        ring.UpdateNodes(new[] { "n1", "n2", "n3", "n4" });

        var first = ring.GetNode("stable-key");
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(first, ring.GetNode("stable-key"));
        }
    }

    [Fact]
    public void UpdateNodes_EmptyCollection_MakesGetNodeReturnNull()
    {
        var ring = new JumpHashConsistentHashRing();
        ring.UpdateNodes(new[] { "node-a" });
        Assert.NotNull(ring.GetNode("key"));

        ring.UpdateNodes(Array.Empty<string>());
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
        ring.UpdateNodes(new[] { "n1", "n2", "n3" });

        var nodes = Enumerable.Range(0, 300).Select(i => ring.GetNode($"key-{i}"))
            .Where(n => n != null)
            .Distinct()
            .ToList();

        Assert.Equal(3, nodes.Count);
    }
}
