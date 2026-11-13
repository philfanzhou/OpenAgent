using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime;

/// <summary>Applies ACP replacement/null/chunk semantics; display summaries never become replay history.</summary>
internal sealed class AcpCompactionMapper(string sessionId)
{
    private const int MaxSummaryCharacters = 65_536;
    private readonly Dictionary<string, State> _states = [];

    internal ContextSummary Map(SessionUpdate update)
    {
        string id = update switch
        {
            CompactionUpdate patch => patch.CompactionId,
            CompactionSummaryChunk chunk => chunk.CompactionId,
            _ => throw new ArgumentException("Expected a compaction update.")
        };
        if (!_states.TryGetValue(id, out State? state))
        {
            if (_states.Count >= 128) throw new InvalidOperationException("Too many compaction entities in one turn.");
            _states[id] = state = new();
        }
        if (update is CompactionUpdate patchUpdate)
        {
            state.Status = patchUpdate.Status;
            if (patchUpdate.Summary.IsDefined)
            {
                state.Summary = string.Concat((patchUpdate.Summary.Value ?? []).OfType<TextContent>().Select(block => block.Text));
            }
            if (patchUpdate.Error.IsDefined)
            {
                // Provider error text can contain credentials; persist only a fixed diagnostic.
                state.Error = patchUpdate.Error.IsNull ? null : "Runtime compaction failed. Refer to the turn TraceId.";
            }
        }
        else if (update is CompactionSummaryChunk { Content: TextContent text })
        {
            state.Summary += text.Text;
        }
        if (state.Summary.Length > MaxSummaryCharacters)
        {
            state.Summary = state.Summary[..MaxSummaryCharacters];
        }
        return new ContextSummary
        {
            CompressionId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\0" + id))),
            Strategy = "runtime", Trigger = "Runtime", IsRuntimeEvent = true,
            RuntimeSessionId = sessionId, RuntimeCompactionId = id,
            Status = state.Status switch
            {
                "completed" => "Succeeded", "failed" => "Failed", "cancelled" => "Cancelled",
                "in_progress" => "InProgress", _ => state.Status
            },
            Summary = state.Summary.Length == 0 ? null : state.Summary,
            Error = state.Error, LastCompressedAt = state.StartedAt
        };
    }

    private sealed class State
    {
        internal string Status = "in_progress";
        internal string Summary = string.Empty;
        internal string? Error;
        internal DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    }
}
