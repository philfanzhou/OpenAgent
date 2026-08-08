using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Routing;

namespace OpenAgent.Router.Routing;

/// <summary>
/// Jump consistent hash implementation used for Router session affinity.
/// </summary>
internal sealed class JumpHashConsistentHashRing : IConsistentHashRing
{
    private string[] _nodes = [];

    public string? GetNode(string key)
    {
        string[] nodes = _nodes;
        if (nodes.Length == 0)
        {
            return null;
        }

        ulong hash = ComputeHash(key);
        int bucket = JumpHash(hash, nodes.Length);
        return nodes[bucket];
    }

    public void UpdateNodes(IEnumerable<string> nodeIds)
    {
        _nodes = nodeIds?.ToArray() ?? [];
    }

    private static ulong ComputeHash(string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        byte[] hash = SHA1.HashData(bytes);
        return BitConverter.ToUInt64(hash, 0);
    }

    private static int JumpHash(ulong key, int bucketCount)
    {
        int bucket = -1;
        int next = 0;
        while (next < bucketCount)
        {
            bucket = next;
            key = key * 2862933555777941757UL + 1;
            next = (int)((bucket + 1) * (double)(1L << 31) / ((key >> 33) + 1));
        }

        return bucket;
    }
}
