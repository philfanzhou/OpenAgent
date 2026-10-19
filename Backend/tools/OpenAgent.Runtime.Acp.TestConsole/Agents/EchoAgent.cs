using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.TestConsole.Agents;

/// <summary>
/// The echo agent behavior shared by the `agent` command and the in-process scenarios: it
/// streams the prompt back as per-grapheme agent_message_chunk updates (Chinese, newlines and
/// emoji stay intact), answers cancelled with one trailing update, and never touches a model.
/// This is an SDK handler definition — all protocol framing lives in the SDK role layer.
/// </summary>
internal static class EchoAgent
{
    internal const string Name = "acp-test-echo";
    internal const string Version = "0.1.0";
    internal const string CancelTail = " [cancelled]";

    /// <summary>
    /// Builds the echo fixture. The first prompt of the connection holds its turn open after
    /// the first chunk for <paramref name="cancelPause"/>, giving cancellation tests a
    /// deterministic window; later turns stream straight through.
    /// </summary>
    public static AcpAgentBuilder CreateBuilder(TimeSpan cancelPause)
    {
        int prompts = 0;
        return new AcpAgentBuilder()
            .WithInfo(Name, Version)
            .HandleNewSession((context, _) =>
            {
                if (!Path.IsPathFullyQualified(context.Cwd))
                {
                    throw new AcpRpcException(new Error(ErrorCode.InvalidParams, "The session cwd must be an absolute path."));
                }

                if (context.McpServers.Length > 0)
                {
                    throw new AcpRpcException(new Error(ErrorCode.InvalidParams, "The echo agent does not connect MCP servers."));
                }

                return Task.CompletedTask;
            })
            .HandlePrompt(async (context, cancellationToken) =>
            {
                try
                {
                    TimeSpan pause = Interlocked.Increment(ref prompts) == 1 ? cancelPause : TimeSpan.Zero;
                    string echo = BuildEcho(context.Prompt);
                    bool first = true;
                    foreach (string segment in Segments(echo))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await context.SendUpdateAsync(TextChunk(segment), cancellationToken).ConfigureAwait(false);
                        if (first && pause != TimeSpan.Zero)
                        {
                            // Holds the turn open so a cancel notification deterministically wins the race.
                            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                        }

                        first = false;
                    }

                    return StopReason.EndTurn;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The cancel-tail update stays ordered before the final cancelled response.
                    await context.SendUpdateAsync(TextChunk(CancelTail), CancellationToken.None).ConfigureAwait(false);
                    return StopReason.Cancelled;
                }
            });
    }

    /// <summary>Composes the echo text: text blocks verbatim, resource links as their URI only (never downloaded).</summary>
    public static string BuildEcho(IReadOnlyList<ContentBlock> prompt)
    {
        StringBuilder echo = new("Echo: ");
        foreach (ContentBlock block in prompt)
        {
            string? type = block.Json.TryGetProperty("type", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
            if (type == "text") echo.Append(block.AsText().Text);
            else if (type == "resource_link") echo.Append(block.AsResourceLink().Uri);
            else echo.Append('…');
        }

        return echo.ToString();
    }

    public static SessionUpdate TextChunk(string text) =>
        new(new SessionUpdateAgentMessageChunk(new ContentBlock(new ContentBlockText(text))));

    /// <summary>Splits on grapheme clusters so composed emoji stream as one piece.</summary>
    public static IEnumerable<string> Segments(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) yield return (string)enumerator.Current!;
    }
}
