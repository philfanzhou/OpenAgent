using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Conversation.Scripts;
using OpenAgent.Core.Conversation.Repository;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Store;

namespace OpenAgent.Core.Conversation.Repository;

internal sealed class SqlServerConversationRepository : IConversationRepository
{
    private readonly ConversationStoreOptions _options;
    private readonly ILogger<SqlServerConversationRepository> _logger;
    private readonly ConversationStoreMetrics _metrics;
    private readonly string _connectionString;
    private readonly SqlServerRetryPolicy _retryPolicy;
    private bool _initialized;

    public SqlServerConversationRepository(
        IOptions<ConversationStoreOptions> options,
        ILogger<SqlServerConversationRepository> logger,
        ConversationStoreMetrics metrics)
    {
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _connectionString = _options.ColdArchiveConnectionString
            ?? throw new InvalidOperationException("ColdArchiveConnectionString is required for SQL Server");
        _retryPolicy = new SqlServerRetryPolicy(
            _options.ColdArchiveRetryCount,
            _options.ColdArchiveRetryDelayMs,
            logger);
    }

    /// <summary>
    /// Archives conversation metadata (without messages).
    /// </summary>
    public async Task ArchiveAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableColdArchive) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await _retryPolicy.ExecuteAsync(
                async () => await UpsertRecordAsync(record, cancellationToken), cancellationToken);

            _metrics.RecordColdArchiveSuccess();
        }
        catch (Exception ex)
        {
            _metrics.RecordColdArchiveFailure();
            ConversationLog.SqlServerArchiveFailed(_logger, ex, record.ConversationId);
        }
        finally
        {
            _metrics.RecordColdArchiveLatency(sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Bulk-archives messages to the row-level message table.
    /// </summary>
    public async Task ArchiveMessagesAsync(
        string tenantId,
        string conversationId,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableColdArchive) return;
        if (messages.Count == 0) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await _retryPolicy.ExecuteAsync(
                async () => await BulkInsertMessagesAsync(tenantId, conversationId, messages, cancellationToken),
                cancellationToken);

            _metrics.RecordColdArchiveSuccess();
        }
        catch (Exception ex)
        {
            _metrics.RecordColdArchiveFailure();
            ConversationLog.SqlServerArchiveMessagesFailed(_logger, ex, conversationId);
        }
        finally
        {
            _metrics.RecordColdArchiveLatency(sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Loads the full message list for a conversation from SQL Server (for cold-to-hot recovery).
    /// </summary>
    public async Task<IReadOnlyList<ConversationMessage>> LoadMessagesAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MessageId, Sequence, Role, Content, ToolCallId, ToolName, Timestamp, MetadataJson
            FROM ConversationMessages
            WHERE TenantId = @TenantId AND ConversationId = @ConversationId
            ORDER BY Sequence ASC";

        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@ConversationId", conversationId);

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
                Timestamp = reader.GetDateTimeOffset(6),
                Metadata = reader.IsDBNull(7) ? null :
                    System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7))
            });
        }

        return messages;
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ConversationId, TenantId, UserId, AgentId, TraceId,
                   Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                   Title, IsDeletedByUser, DeletedAt, ArchivedAt
            FROM ConversationRecords
            WHERE TenantId = @TenantId AND ConversationId = @ConversationId";

        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@ConversationId", conversationId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var record = new ConversationRecord
        {
            ConversationId = reader.GetString(0),
            TenantId = reader.GetString(1),
            UserId = reader.GetString(2),
            AgentId = reader.IsDBNull(3) ? null : reader.GetString(3),
            TraceId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Version = reader.GetInt32(5),
            Status = (ConversationStatus)reader.GetInt32(6),
            CreatedAt = reader.GetDateTimeOffset(7),
            UpdatedAt = reader.GetDateTimeOffset(8),
            LastMessageAt = reader.GetDateTimeOffset(9),
            MessageCount = reader.GetInt32(10),
            Title = reader.IsDBNull(11) ? null : reader.GetString(11),
            IsDeletedByUser = reader.GetBoolean(12),
            DeletedAt = reader.IsDBNull(13) ? null : reader.GetDateTimeOffset(13),
            ArchivedAt = reader.GetDateTimeOffset(14)
        };

        var messages = await LoadMessagesAsync(tenantId, conversationId, cancellationToken);
        record.Messages = messages.ToList();

        return record;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ConversationScripts.SqlServerSchema;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        catch (Exception ex)
        {
            ConversationLog.SqlServerInitializeFailed(_logger, ex);
            throw;
        }
    }

    private async Task UpsertRecordAsync(ConversationRecord record, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            MERGE INTO ConversationRecords AS target
            USING (VALUES (@ConversationId, @TenantId, @UserId, @AgentId, @TraceId,
                           @Version, @Status, @CreatedAt, @UpdatedAt, @LastMessageAt, @MessageCount,
                           @Title, @IsDeletedByUser, @DeletedAt, @ArchivedAt))
            AS source (ConversationId, TenantId, UserId, AgentId, TraceId,
                       Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                       Title, IsDeletedByUser, DeletedAt, ArchivedAt)
            ON target.ConversationId = source.ConversationId
            WHEN MATCHED THEN
                UPDATE SET
                    AgentId = source.AgentId,
                    TraceId = source.TraceId,
                    Version = source.Version,
                    Status = source.Status,
                    UpdatedAt = source.UpdatedAt,
                    LastMessageAt = source.LastMessageAt,
                    MessageCount = source.MessageCount,
                    Title = source.Title,
                    IsDeletedByUser = source.IsDeletedByUser,
                    DeletedAt = source.DeletedAt,
                    ArchivedAt = source.ArchivedAt
            WHEN NOT MATCHED THEN
                INSERT (ConversationId, TenantId, UserId, AgentId, TraceId,
                        Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                        Title, IsDeletedByUser, DeletedAt, ArchivedAt)
                VALUES (source.ConversationId, source.TenantId, source.UserId, source.AgentId, source.TraceId,
                        source.Version, source.Status, source.CreatedAt, source.UpdatedAt, source.LastMessageAt, source.MessageCount,
                        source.Title, source.IsDeletedByUser, source.DeletedAt, source.ArchivedAt);";

        cmd.Parameters.AddWithValue("@ConversationId", record.ConversationId);
        cmd.Parameters.AddWithValue("@TenantId", record.TenantId);
        cmd.Parameters.AddWithValue("@UserId", record.UserId);
        cmd.Parameters.AddWithValue("@AgentId", (object?)record.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TraceId", (object?)record.TraceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Version", record.Version);
        cmd.Parameters.AddWithValue("@Status", (int)record.Status);
        cmd.Parameters.AddWithValue("@CreatedAt", record.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", record.UpdatedAt);
        cmd.Parameters.AddWithValue("@LastMessageAt", record.LastMessageAt);
        cmd.Parameters.AddWithValue("@MessageCount", record.MessageCount);
        cmd.Parameters.AddWithValue("@Title", (object?)record.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsDeletedByUser", record.IsDeletedByUser ? 1 : 0);
        cmd.Parameters.AddWithValue("@DeletedAt", (object?)record.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ArchivedAt", record.ArchivedAt);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task BulkInsertMessagesAsync(
        string tenantId,
        string conversationId,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var table = new DataTable();
        table.Columns.Add("ConversationId", typeof(string));
        table.Columns.Add("Sequence", typeof(int));
        table.Columns.Add("MessageId", typeof(string));
        table.Columns.Add("Role", typeof(string));
        table.Columns.Add("Content", typeof(string));
        table.Columns.Add("ToolCallId", typeof(string));
        table.Columns.Add("ToolName", typeof(string));
        table.Columns.Add("Timestamp", typeof(DateTimeOffset));
        table.Columns.Add("MetadataJson", typeof(string));

        foreach (var msg in messages)
        {
            table.Rows.Add(
                conversationId,
                msg.Sequence,
                msg.MessageId,
                msg.Role,
                msg.Content,
                (object?)msg.ToolCallId ?? DBNull.Value,
                (object?)msg.ToolName ?? DBNull.Value,
                msg.Timestamp,
                msg.Metadata != null
                    ? System.Text.Json.JsonSerializer.Serialize(msg.Metadata)
                    : DBNull.Value);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            MERGE INTO ConversationMessages AS target
            USING @messages AS source
            ON target.ConversationId = source.ConversationId AND target.Sequence = source.Sequence
            WHEN NOT MATCHED THEN
                INSERT (ConversationId, Sequence, MessageId, Role, Content,
                        ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
                VALUES (source.ConversationId, source.Sequence, source.MessageId, source.Role, source.Content,
                        source.ToolCallId, source.ToolName, source.Timestamp, source.MetadataJson, @TenantId);";

        cmd.Parameters.AddWithValue("@TenantId", tenantId);

        var tvp = cmd.Parameters.AddWithValue("@messages", table);
        tvp.SqlDbType = SqlDbType.Structured;
        tvp.TypeName = "dbo.ConversationMessageType";

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose()
    {
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ConversationId, TenantId, UserId, AgentId, TraceId,
                   Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount,
                   Title, IsDeletedByUser, DeletedAt, ArchivedAt
            FROM ConversationRecords
            WHERE TenantId = @TenantId AND IsDeletedByUser = 0
            ORDER BY LastMessageAt DESC
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Skip", skip);
        cmd.Parameters.AddWithValue("@Take", take);

        var records = new List<ConversationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ConversationRecord
            {
                ConversationId = reader.GetString(0),
                TenantId = reader.GetString(1),
                UserId = reader.GetString(2),
                AgentId = reader.IsDBNull(3) ? null : reader.GetString(3),
                TraceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Version = reader.GetInt32(5),
                Status = (ConversationStatus)reader.GetInt32(6),
                CreatedAt = reader.GetDateTimeOffset(7),
                UpdatedAt = reader.GetDateTimeOffset(8),
                LastMessageAt = reader.GetDateTimeOffset(9),
                MessageCount = reader.GetInt32(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                IsDeletedByUser = reader.GetBoolean(12),
                DeletedAt = reader.IsDBNull(13) ? null : reader.GetDateTimeOffset(13),
                ArchivedAt = reader.GetDateTimeOffset(14)
            });
        }

        return records;
    }

    public async Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT r.ConversationId, r.TenantId, r.UserId, r.AgentId, r.TraceId,
                   r.Version, r.Status, r.CreatedAt, r.UpdatedAt, r.LastMessageAt, r.MessageCount,
                   r.Title, r.IsDeletedByUser, r.DeletedAt, r.ArchivedAt
            FROM ConversationRecords r
            INNER JOIN ConversationMessages m
                ON r.ConversationId = m.ConversationId
            WHERE r.TenantId = @TenantId AND r.IsDeletedByUser = 0 AND (r.Title LIKE '%' + @Keyword + '%' OR m.Content LIKE '%' + @Keyword + '%')
            GROUP BY r.ConversationId, r.TenantId, r.UserId, r.AgentId, r.TraceId,
                     r.Version, r.Status, r.CreatedAt, r.UpdatedAt, r.LastMessageAt, r.MessageCount,
                     r.Title, r.IsDeletedByUser, r.DeletedAt, r.ArchivedAt
            ORDER BY MAX(r.LastMessageAt) DESC
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Keyword", keyword);
        cmd.Parameters.AddWithValue("@Skip", skip);
        cmd.Parameters.AddWithValue("@Take", take);

        var records = new List<ConversationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ConversationRecord
            {
                ConversationId = reader.GetString(0),
                TenantId = reader.GetString(1),
                UserId = reader.GetString(2),
                AgentId = reader.IsDBNull(3) ? null : reader.GetString(3),
                TraceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Version = reader.GetInt32(5),
                Status = (ConversationStatus)reader.GetInt32(6),
                CreatedAt = reader.GetDateTimeOffset(7),
                UpdatedAt = reader.GetDateTimeOffset(8),
                LastMessageAt = reader.GetDateTimeOffset(9),
                MessageCount = reader.GetInt32(10),
                Title = reader.IsDBNull(11) ? null : reader.GetString(11),
                IsDeletedByUser = reader.GetBoolean(12),
                DeletedAt = reader.IsDBNull(13) ? null : reader.GetDateTimeOffset(13),
                ArchivedAt = reader.GetDateTimeOffset(14)
            });
        }

        return records;
    }
}
