using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Conversation.Scripts;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Store;

namespace OpenAgent.Core.Conversation.Repository;

internal sealed class SqliteConversationRepository : IConversationRepository
{
    private readonly ConversationStoreOptions _options;
    private readonly ILogger<SqliteConversationRepository> _logger;
    private readonly ConversationStoreMetrics _metrics;
    private readonly string _connectionString;
    private bool _initialized;

    public SqliteConversationRepository(
        IOptions<ConversationStoreOptions> options,
        ILogger<SqliteConversationRepository> logger,
        ConversationStoreMetrics metrics)
    {
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _connectionString = _options.ColdArchiveConnectionString
            ?? throw new InvalidOperationException("ColdArchiveConnectionString is required for SQLite");
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ConversationScripts.SqliteSchema;

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Add new columns individually. SQLite doesn't support IF NOT EXISTS for
            // ADD COLUMN, so each ALTER is guarded by try-catch to handle "duplicate
            // column name" errors from existing databases.
            var alterStatements = new[]
            {
                "ALTER TABLE ConversationRecords ADD COLUMN Title TEXT;",
                "ALTER TABLE ConversationRecords ADD COLUMN IsDeletedByUser INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE ConversationRecords ADD COLUMN DeletedAt TEXT;",
                "ALTER TABLE ConversationRecords ADD COLUMN ArchivedAt TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00';"
            };
            foreach (var alter in alterStatements)
            {
                try
                {
                    await using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = alter;
                    await alterCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                    // Column already exists — ignore
                }
            }

            await using var indexCmd = conn.CreateCommand();
            indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Records_Tenant_Deleted ON ConversationRecords (TenantId, IsDeletedByUser, LastMessageAt);";
            await indexCmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        catch (Exception ex)
        {
            ConversationLog.SqliteInitializeFailed(_logger, ex);
            throw;
        }
    }

    public async Task ArchiveAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableColdArchive) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await RetryAsync(async () => await UpsertRecordAsync(record, cancellationToken), cancellationToken);

            _metrics.RecordColdArchiveSuccess();
        }
        catch (Exception ex)
        {
            _metrics.RecordColdArchiveFailure();
            ConversationLog.SqliteArchiveFailed(_logger, ex, record.ConversationId);
        }
        finally
        {
            _metrics.RecordColdArchiveLatency(sw.ElapsedMilliseconds);
        }
    }

    public async Task ArchiveMessagesAsync(
        string tenantId, string conversationId,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableColdArchive) return;
        if (messages.Count == 0) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await RetryAsync(async () => await BulkInsertMessagesAsync(tenantId, conversationId, messages, cancellationToken), cancellationToken);

            _metrics.RecordColdArchiveSuccess();
        }
        catch (Exception ex)
        {
            _metrics.RecordColdArchiveFailure();
            ConversationLog.SqliteArchiveMessagesFailed(_logger, ex, conversationId);
        }
        finally
        {
            _metrics.RecordColdArchiveLatency(sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<ConversationMessage>> LoadMessagesAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MessageId, Sequence, Role, Content, ToolCallId, ToolName, Timestamp, MetadataJson
            FROM ConversationMessages
            WHERE TenantId = $TenantId AND ConversationId = $ConversationId
            ORDER BY Sequence ASC";

        cmd.Parameters.AddWithValue("$TenantId", tenantId);
        cmd.Parameters.AddWithValue("$ConversationId", conversationId);

        return await ReadMessagesAsync(cmd, cancellationToken);
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ConversationId, TenantId, UserId, AgentId, TraceId,
                   Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                   Title, IsDeletedByUser, DeletedAt, ArchivedAt
            FROM ConversationRecords
            WHERE TenantId = $TenantId AND ConversationId = $ConversationId";

        cmd.Parameters.AddWithValue("$TenantId", tenantId);
        cmd.Parameters.AddWithValue("$ConversationId", conversationId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var record = ReadRecord(reader);
        var messages = await LoadMessagesAsync(tenantId, conversationId, cancellationToken);
        record.Messages = messages.ToList();

        return record;
    }

    private async Task UpsertRecordAsync(ConversationRecord record, CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ConversationRecords
                (ConversationId, TenantId, UserId, AgentId, TraceId,
                 Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                 Title, IsDeletedByUser, DeletedAt, ArchivedAt)
            VALUES
                ($ConversationId, $TenantId, $UserId, $AgentId, $TraceId,
                 $Version, $Status, $CreatedAt, $UpdatedAt, $LastMessageAt, $MessageCount,
                 $Title, $IsDeletedByUser, $DeletedAt, $ArchivedAt)
            ON CONFLICT(ConversationId) DO UPDATE SET
                AgentId = excluded.AgentId,
                TraceId = excluded.TraceId,
                Version = excluded.Version,
                Status = excluded.Status,
                UpdatedAt = excluded.UpdatedAt,
                LastMessageAt = excluded.LastMessageAt,
                MessageCount = excluded.MessageCount,
                Title = excluded.Title,
                IsDeletedByUser = excluded.IsDeletedByUser,
                DeletedAt = excluded.DeletedAt,
                ArchivedAt = excluded.ArchivedAt";

        cmd.Parameters.AddWithValue("$ConversationId", record.ConversationId);
        cmd.Parameters.AddWithValue("$TenantId", record.TenantId);
        cmd.Parameters.AddWithValue("$UserId", record.UserId);
        cmd.Parameters.AddWithValue("$AgentId", (object?)record.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$TraceId", (object?)record.TraceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Version", record.Version);
        cmd.Parameters.AddWithValue("$Status", (int)record.Status);
        cmd.Parameters.AddWithValue("$CreatedAt", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$UpdatedAt", record.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$LastMessageAt", record.LastMessageAt.ToString("O"));
        cmd.Parameters.AddWithValue("$MessageCount", record.MessageCount);
        cmd.Parameters.AddWithValue("$Title", (object?)record.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$IsDeletedByUser", record.IsDeletedByUser ? 1 : 0);
        cmd.Parameters.AddWithValue("$DeletedAt", (object?)record.DeletedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ArchivedAt", record.ArchivedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task BulkInsertMessagesAsync(
        string tenantId, string conversationId,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var tx = conn.BeginTransaction();

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO ConversationMessages
                (ConversationId, Sequence, MessageId, Role, Content,
                 ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
            VALUES
                ($ConversationId, $Sequence, $MessageId, $Role, $Content,
                 $ToolCallId, $ToolName, $Timestamp, $MetadataJson, $TenantId)";

        var cidParam = cmd.Parameters.Add("$ConversationId", SqliteType.Text);
        var seqParam = cmd.Parameters.Add("$Sequence", SqliteType.Integer);
        var midParam = cmd.Parameters.Add("$MessageId", SqliteType.Text);
        var roleParam = cmd.Parameters.Add("$Role", SqliteType.Text);
        var contentParam = cmd.Parameters.Add("$Content", SqliteType.Text);
        var tcidParam = cmd.Parameters.Add("$ToolCallId", SqliteType.Text);
        var tnameParam = cmd.Parameters.Add("$ToolName", SqliteType.Text);
        var tsParam = cmd.Parameters.Add("$Timestamp", SqliteType.Text);
        var metaParam = cmd.Parameters.Add("$MetadataJson", SqliteType.Text);
        var tenantParam = cmd.Parameters.Add("$TenantId", SqliteType.Text);

        foreach (var msg in messages)
        {
            cidParam.Value = conversationId;
            seqParam.Value = msg.Sequence;
            midParam.Value = msg.MessageId;
            roleParam.Value = msg.Role;
            contentParam.Value = msg.Content;
            tcidParam.Value = (object?)msg.ToolCallId ?? DBNull.Value;
            tnameParam.Value = (object?)msg.ToolName ?? DBNull.Value;
            tsParam.Value = msg.Timestamp.ToString("O");
            metaParam.Value = msg.Metadata != null
                ? System.Text.Json.JsonSerializer.Serialize(msg.Metadata)
                : DBNull.Value;
            tenantParam.Value = tenantId;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(
        SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var messages = new List<ConversationMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ConversationMessage
            {
                MessageId = reader.GetString(0),
                Sequence = reader.GetInt32(1),
                Role = reader.GetString(2),
                Content = reader.GetString(3),
                ToolCallId = reader.IsDBNull(4) ? null : reader.GetString(4),
                ToolName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Timestamp = DateTimeOffset.Parse(reader.GetString(6)),
                Metadata = reader.IsDBNull(7) ? null :
                    System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7))
            });
        }
        return messages;
    }

    private static ConversationRecord ReadRecord(System.Data.Common.DbDataReader reader)
    {
        return new ConversationRecord
        {
            ConversationId = reader.GetString(0),
            TenantId = reader.GetString(1),
            UserId = reader.GetString(2),
            AgentId = reader.IsDBNull(3) ? null : reader.GetString(3),
            TraceId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Version = reader.GetInt32(5),
            Status = (ConversationStatus)reader.GetInt32(6),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(8)),
            LastMessageAt = DateTimeOffset.Parse(reader.GetString(9)),
            MessageCount = reader.GetInt32(10),
            Title = reader.IsDBNull(11) ? null : reader.GetString(11),
            IsDeletedByUser = reader.GetInt32(12) != 0,
            DeletedAt = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            ArchivedAt = ParseDateTimeOffsetSafe(reader, 14, DateTimeOffset.MinValue)
        };
    }

    private static DateTimeOffset ParseDateTimeOffsetSafe(System.Data.Common.DbDataReader reader, int ordinal, DateTimeOffset fallback)
    {
        if (reader.IsDBNull(ordinal)) return fallback;
        var value = reader.GetString(ordinal);
        return string.IsNullOrWhiteSpace(value) ? fallback : DateTimeOffset.Parse(value);
    }

    private async Task RetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var retryCount = _options.ColdArchiveRetryCount;
        var delayMs = _options.ColdArchiveRetryDelayMs;

        for (int attempt = 0; attempt <= retryCount; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < retryCount)
            {
                ConversationLog.SqliteRetryAttemptFailed(_logger, ex, attempt + 1, delayMs);
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2;
            }
        }
    }

    public void Dispose()
    {
        // Clear the SQLite connection pool so the database file can be deleted
        SqliteConnection.ClearAllPools();
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ConversationId, TenantId, UserId, AgentId, TraceId,
                   Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                   Title, IsDeletedByUser, DeletedAt, ArchivedAt
            FROM ConversationRecords
            WHERE TenantId = $TenantId AND IsDeletedByUser = 0
            ORDER BY LastMessageAt DESC
            LIMIT $Take OFFSET $Skip";

        cmd.Parameters.AddWithValue("$TenantId", tenantId);
        cmd.Parameters.AddWithValue("$Skip", skip);
        cmd.Parameters.AddWithValue("$Take", take);

        var records = new List<ConversationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT r.ConversationId, r.TenantId, r.UserId, r.AgentId, r.TraceId,
                   r.Version, r.Status, r.CreatedAt, r.UpdatedAt, r.LastMessageAt, r.MessageCount,
                   r.Title, r.IsDeletedByUser, r.DeletedAt, r.ArchivedAt
            FROM ConversationRecords r
            INNER JOIN ConversationMessages m
                ON r.ConversationId = m.ConversationId
            WHERE r.TenantId = $TenantId AND r.IsDeletedByUser = 0 AND (r.Title LIKE '%' || $Keyword || '%' OR m.Content LIKE '%' || $Keyword || '%')
            ORDER BY r.LastMessageAt DESC
            LIMIT $Take OFFSET $Skip";

        cmd.Parameters.AddWithValue("$TenantId", tenantId);
        cmd.Parameters.AddWithValue("$Keyword", keyword);
        cmd.Parameters.AddWithValue("$Skip", skip);
        cmd.Parameters.AddWithValue("$Take", take);

        var records = new List<ConversationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }
}
