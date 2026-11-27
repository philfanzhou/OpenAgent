using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeSessionDatabaseTests : IDisposable
{
    private const string Session = "ses_native";
    private const string OtherSession = "ses_other";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode-session-db-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadSummariesAsync_SeededSession_MatchesWhatTheExportParserReturns()
    {
        string database = CreateDatabase(connection =>
        {
            // 与真实库一致：id 只在列里，data 中没有。
            InsertMessage(connection, "user", 1, """{"role":"user"}""");
            InsertMessage(connection, "reply", 2, """{"role":"assistant","time":{"completed":2}}""");
            InsertPart(connection, "reply-text", 2, "reply", """{"type":"text","text":"normal reply"}""");
            InsertMessage(connection, "summary-failed", 3, """{"role":"assistant","summary":true,"error":{"data":"boom"}}""");
            InsertMessage(connection, "summary-done", 4, """{"role":"assistant","summary":true,"time":{"created":4,"completed":5}}""");
            InsertPart(connection, "done-b", 6, "summary-done", """{"type":"text","text":" part B"}""");
            InsertPart(connection, "done-a", 5, "summary-done", """{"type":"text","text":"part A"}""");
            InsertPart(connection, "done-hidden", 7, "summary-done", """{"type":"reasoning","text":"hidden"}""");
            InsertPart(connection, "foreign", 8, "summary-done", """{"type":"text","text":"other session"}""", OtherSession);
        });
        HashSet<string> reported = [];

        List<ContextSummary> audits = [.. await OpenCodeSessionDatabase.ReadSummariesAsync(database, Session, reported, CancellationToken.None)];

        // 普通回复、用户消息、非文本分片和其它会话的内容都不出现；文本分片按创建时间拼接。
        Assert.Equal(["summary-failed", "summary-done"], audits.Select(item => item.RuntimeCompactionId));
        Assert.Equal(["Failed", "Succeeded"], audits.Select(item => item.Status));
        Assert.Equal("part A part B", audits[1].Summary);
        Assert.Equal(["summary-done", "summary-failed"], reported.Order(StringComparer.Ordinal));
        using JsonDocument export = JsonDocument.Parse("""
            {"info":{"id":"ses_native"},"messages":[
              {"info":{"id":"summary-failed","role":"assistant","summary":true,"error":{"data":"boom"}},"parts":[]},
              {"info":{"id":"summary-done","role":"assistant","summary":true,"time":{"created":4,"completed":5}},
               "parts":[{"type":"text","text":"part A"},{"type":"text","text":" part B"},{"type":"reasoning","text":"hidden"}]}
            ]}
            """);
        List<ContextSummary> expected = [.. OpenCodeCompactionExport.ReadSummaries(export.RootElement, Session, [])];
        Assert.Equal(
            expected.Select(item => (item.RuntimeCompactionId, item.Status, item.Summary, item.IsRuntimeEvent, item.CompactedMessages.Count)),
            audits.Select(item => (item.RuntimeCompactionId, item.Status, item.Summary, item.IsRuntimeEvent, item.CompactedMessages.Count)));
    }

    [Fact]
    public async Task ReadSummariesAsync_SummaryAlreadyReported_IsNotReturnedAgain()
    {
        string database = CreateDatabase(connection =>
        {
            InsertMessage(connection, "summary", 1, """{"role":"assistant","summary":true,"time":{"completed":1}}""");
            InsertPart(connection, "summary-text", 2, "summary", """{"type":"text","text":"known"}""");
        });

        Assert.Empty(await OpenCodeSessionDatabase.ReadSummariesAsync(database, Session, ["summary"], CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadSummariesAsync_DatabaseUnreadable_ThrowsSqliteException(bool emptyDatabase)
    {
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
        string database = Path.Combine(_root, "opencode.db");
        if (emptyDatabase)
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            connection.Open();
        }

        // 文件不存在或表结构不符（例如上游升级后）时抛出，由调用方回退到导出命令。
        await Assert.ThrowsAsync<SqliteException>(() => OpenCodeSessionDatabase.ReadSummariesAsync(database, Session, [], CancellationToken.None));
    }

    [Fact]
    public async Task ReadSummariesAsync_WhileHarnessHoldsAnOpenWriteTransaction_ReadsCommittedRowsOnly()
    {
        string database = CreateDatabase(connection =>
        {
            using SqliteCommand wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL";
            wal.ExecuteNonQuery();
            InsertMessage(connection, "committed", 1, """{"role":"assistant","summary":true,"time":{"completed":1}}""");
        });
        await using SqliteConnection harness = new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await harness.OpenAsync();
        using SqliteTransaction pending = (SqliteTransaction)await harness.BeginTransactionAsync();
        using (SqliteCommand insert = harness.CreateCommand())
        {
            insert.Transaction = pending;
            insert.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) "
                + "VALUES ('pending', 'ses_native', 2, 2, '{\"role\":\"assistant\",\"summary\":true}')";
            await insert.ExecuteNonQueryAsync();
        }

        // OpenCode 进程持有未提交的写事务时读取：不阻塞，也看不到未提交的行。
        Task<IReadOnlyList<ContextSummary>> read = OpenCodeSessionDatabase.ReadSummariesAsync(database, Session, [], CancellationToken.None);
        IReadOnlyList<ContextSummary> audits = await read.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("committed", Assert.Single(audits).RuntimeCompactionId);
    }

    [Fact]
    public async Task ReadSummariesAsync_ThroughTheDispatcher_DoesNotInvokeTheExportCommand()
    {
        string database = CreateDatabase(connection =>
        {
            InsertMessage(connection, "summary", 1, """{"role":"assistant","summary":true,"time":{"completed":1}}""");
            InsertPart(connection, "summary-text", 2, "summary", """{"type":"text","text":"from database"}""");
        });

        IReadOnlyList<ContextSummary> audits = await OpenCodeCompactionExport.ReadSummariesAsync(database, Session, [],
            _ => throw new InvalidOperationException("The export command must not run while the database is readable."),
            NullLogger.Instance, CancellationToken.None);

        Assert.Equal("from database", Assert.Single(audits).Summary);
    }

    // OpenCode 1.x 的表结构（drizzle 生成）。
    private string CreateDatabase(Action<SqliteConnection> seed)
    {
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
        string database = Path.Combine(_root, "opencode.db");
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE `message` (`id` text PRIMARY KEY, `session_id` text NOT NULL, `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL, `data` text NOT NULL);
            CREATE TABLE `part` (`id` text PRIMARY KEY, `message_id` text NOT NULL, `session_id` text NOT NULL,
              `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `data` text NOT NULL);
            """;
        schema.ExecuteNonQuery();
        seed(connection);
        return database;
    }

    private static void InsertMessage(SqliteConnection connection, string id, long time, string data, string session = Session)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ($id, $session, $time, $time, $data)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$session", session);
        insert.Parameters.AddWithValue("$time", time);
        insert.Parameters.AddWithValue("$data", data);
        insert.ExecuteNonQuery();
    }

    private static void InsertPart(SqliteConnection connection, string id, long time, string messageId, string data, string session = Session)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ($id, $message, $session, $time, $time, $data)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$message", messageId);
        insert.Parameters.AddWithValue("$session", session);
        insert.Parameters.AddWithValue("$time", time);
        insert.Parameters.AddWithValue("$data", data);
        insert.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
