using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>
/// 直接读取 OpenCode 原生会话数据库里的压缩摘要。OpenCode 1.x 不发标准 ACP 压缩事件，摘要以
/// <c>assistant.summary=true</c> 的消息存放在进程数据目录的 SQLite 库中；自带的 <c>opencode export</c>
/// 读的也是这份数据，但要为此再启动一个 OpenCode 进程。
/// </summary>
internal static class OpenCodeSessionDatabase
{
    /// <summary>
    /// 按导出命令相同的 JSON 形状组装结果，再交给既有的解析逻辑，保证两条路径读出同样的摘要。
    /// 文件不存在、表结构不符等情况抛出 <see cref="SqliteException"/>，由调用方决定是否回退到导出命令。
    /// </summary>
    internal static async Task<IReadOnlyList<ContextSummary>> ReadSummariesAsync(
        string database, string sessionId, HashSet<string> reported, CancellationToken ct)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // 消息行只含元数据，逐行解析后留下角色为 assistant 且标记为摘要的消息。
        List<SummaryMessage> summaries = [];
        await using (SqliteCommand messages = connection.CreateCommand())
        {
            messages.CommandText = "SELECT id, data FROM message WHERE session_id = $session ORDER BY time_created, id";
            messages.Parameters.AddWithValue("$session", sessionId);
            await using SqliteDataReader reader = await messages.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                using JsonDocument info = JsonDocument.Parse(reader.GetString(1));
                JsonElement root = info.RootElement;
                if (root.GetProperty("role").GetString() != "assistant"
                    || !root.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.True)
                {
                    continue;
                }
                summaries.Add(new SummaryMessage(reader.GetString(0), root.Clone()));
            }
        }

        // 正文分片只取摘要消息的；普通消息的分片（工具输出等）体积大，与摘要无关。
        Dictionary<string, List<JsonElement>> parts = new(StringComparer.Ordinal);
        if (summaries.Count > 0)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT message_id, data FROM part WHERE session_id = $session AND message_id IN ("
                + string.Join(", ", Enumerable.Range(0, summaries.Count).Select(index => "$id" + index))
                + ") ORDER BY time_created, id";
            command.Parameters.AddWithValue("$session", sessionId);
            for (int index = 0; index < summaries.Count; index++)
            {
                command.Parameters.AddWithValue("$id" + index, summaries[index].Id);
            }
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                using JsonDocument part = JsonDocument.Parse(reader.GetString(1));
                string messageId = reader.GetString(0);
                if (!parts.TryGetValue(messageId, out List<JsonElement>? messageParts))
                {
                    parts[messageId] = messageParts = [];
                }
                messageParts.Add(part.RootElement.Clone());
            }
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("info");
            writer.WriteString("id", sessionId);
            writer.WriteEndObject();
            writer.WriteStartArray("messages");
            foreach (SummaryMessage summary in summaries)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("info");
                writer.WriteStartObject();
                // OpenCode 只把消息 id 存在列里，data 中没有；导出命令输出时才合并进来。
                foreach (JsonProperty property in summary.Info.EnumerateObject()) property.WriteTo(writer);
                writer.WriteString("id", summary.Id);
                writer.WriteEndObject();
                writer.WriteStartArray("parts");
                foreach (JsonElement part in parts.GetValueOrDefault(summary.Id) ?? [])
                {
                    part.WriteTo(writer);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using JsonDocument export = JsonDocument.Parse(buffer.ToArray());
        return OpenCodeCompactionExport.ReadSummaries(export.RootElement, sessionId, reported);
    }

    private sealed record SummaryMessage(string Id, JsonElement Info);
}
