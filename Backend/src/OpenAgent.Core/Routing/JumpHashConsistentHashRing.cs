using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Routing;

namespace OpenAgent.Core.Routing;

/// <summary>
/// Jump consistent hash ring implementation.
/// Provides minimal key redistribution when nodes are added or removed.
/// </summary>
public sealed class JumpHashConsistentHashRing : IConsistentHashRing
{
    private string[] _nodes = Array.Empty<string>();

    public string? GetNode(string key)
    {
        var nodes = _nodes;
        if (nodes.Length == 0)
        {
            return null;
        }

        var hash = ComputeHash(key);
        var bucket = JumpHash(hash, nodes.Length);
        return nodes[bucket];
    }

    public void UpdateNodes(IEnumerable<string> nodeIds)
    {
        var nodes = nodeIds?.ToArray() ?? Array.Empty<string>();
        _nodes = nodes;
    }

    private static ulong ComputeHash(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
#if NET8_0_OR_GREATER
        var hash = SHA1.HashData(bytes);
#else
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(bytes);
#endif
        return BitConverter.ToUInt64(hash, 0);
    }

    private static int JumpHash(ulong key, int numBuckets)
    {
        int b = -1;
        int j = 0;

        while (j < numBuckets)
        {
            b = j;
            key = key * 2862933555777941757UL + 1;
            j = (int)((b + 1) * (double)(1L << 31) / ((key >> 33) + 1));
        }

        return b;
    }
}
