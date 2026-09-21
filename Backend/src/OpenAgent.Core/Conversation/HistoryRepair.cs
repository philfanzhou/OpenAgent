using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// 把存储展开的并行 tool_calls 修复回 provider 期望的配对契约：折叠连续 assistant
/// 分片、丢弃未应答/重复调用、保留每个调用的响应。纯函数，无 I/O 无状态。
/// </summary>
internal static class HistoryRepair
{
    internal static List<ChatMessage> RepairToolHistory(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string> responded = [];
        foreach (ChatMessage message in messages)
        {
            foreach (FunctionResultContent result in message.Contents.OfType<FunctionResultContent>())
            {
                if (!string.IsNullOrWhiteSpace(result.CallId))
                {
                    responded.Add(result.CallId);
                }
            }
        }

        HashSet<string> announced = new(StringComparer.Ordinal);
        var repaired = new List<ChatMessage>(messages.Count);
        foreach (ChatMessage message in messages)
        {
            List<FunctionCallContent> calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (message.Role != ChatRole.Assistant || calls.Count == 0)
            {
                repaired.Add(message);
                continue;
            }

            List<FunctionCallContent> retained = calls
                .Where(call => !string.IsNullOrWhiteSpace(call.CallId)
                    && announced.Add(call.CallId)
                    && responded.Contains(call.CallId))
                .ToList();

            // Consecutive assistant rows are fragments of one turn's parallel calls;
            // fold each fragment's non-call contents and retained calls into the block
            // emitted before it so the responses that follow apply to all of them.
            if (repaired.Count > 0
                && repaired[^1].Role == ChatRole.Assistant
                && repaired[^1].Contents.OfType<FunctionCallContent>().Any())
            {
                var merged = new List<AIContent>(repaired[^1].Contents);
                foreach (AIContent content in message.Contents)
                {
                    if (content is not FunctionCallContent)
                    {
                        merged.Add(content);
                    }
                }
                foreach (FunctionCallContent call in retained)
                {
                    merged.Add(call);
                }
                repaired[^1] = new ChatMessage(ChatRole.Assistant, merged);
                continue;
            }

            if (retained.Count == 0)
            {
                continue;
            }

            if (retained.Count < calls.Count)
            {
                ChatMessage rebuilt = new(message.Role, Array.Empty<AIContent>());
                foreach (AIContent content in message.Contents)
                {
                    if (content is not FunctionCallContent)
                    {
                        rebuilt.Contents.Add(content);
                    }
                }

                foreach (FunctionCallContent call in retained)
                {
                    rebuilt.Contents.Add(call);
                }

                repaired.Add(rebuilt);
                continue;
            }

            repaired.Add(message);
        }

        return repaired;
    }
}
