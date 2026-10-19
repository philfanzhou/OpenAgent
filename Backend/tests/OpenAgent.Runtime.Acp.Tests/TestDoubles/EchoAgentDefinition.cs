using System.Globalization;
using System.Text;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

/// <summary>
/// Test-side echo agent definition mirroring the historical demo behavior: stream the prompt
/// back per grapheme, answer cancelled with a trailing update, and no model access. The first
/// prompt of the connection parks after its first chunk for <paramref name="cancelPause"/> so
/// cancellation races are deterministic.
/// </summary>
public static class EchoAgentDefinition
{
    public const string Name = "test-echo";
    public const string Version = "0.1.0";
    public const string CancelTail = " [cancelled]";

    public static AcpAgentBuilder CreateBuilder(TimeSpan cancelPause)
    {
        int prompts = 0;
        return new AcpAgentBuilder()
            .WithInfo(Name, Version)
            .HandleNewSession((context, _) =>
            {
                if (context.McpServers.Length > 0)
                {
                    throw new AcpRpcException(new Error(ErrorCode.InvalidParams, "The echo definition does not connect MCP servers."));
                }

                return Task.CompletedTask;
            })
            .HandlePrompt(async (context, cancellationToken) =>
            {
                try
                {
                    TimeSpan pause = Interlocked.Increment(ref prompts) == 1 ? cancelPause : TimeSpan.Zero;
                    string echo = "Echo: " + TextOf(context.Prompt);
                    bool first = true;
                    foreach (string segment in Segments(echo))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await context.SendUpdateAsync(Chunk(segment), cancellationToken);
                        if (first && pause != TimeSpan.Zero)
                        {
                            await Task.Delay(pause, cancellationToken);
                        }

                        first = false;
                    }

                    return StopReason.EndTurn;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await context.SendUpdateAsync(Chunk(CancelTail), CancellationToken.None);
                    return StopReason.Cancelled;
                }
            });
    }

    private static string TextOf(IReadOnlyList<ContentBlock> prompt)
    {
        StringBuilder text = new();
        foreach (ContentBlock block in prompt)
        {
            if (block.Json.TryGetProperty("type", out var tag) && tag.GetString() == "text") text.Append(block.AsText().Text);
        }

        return text.ToString();
    }

    private static SessionUpdate Chunk(string value) =>
        new(new SessionUpdateAgentMessageChunk(new ContentBlock(new ContentBlockText(value))));

    private static IEnumerable<string> Segments(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) yield return (string)enumerator.Current!;
    }
}
