using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Store;

/// <summary>
/// 后台定时任务：将超过保留周期的消息从 ConversationMessages 迁移到 ConversationMessagesArchive。
/// 仅在 SQL Server 冷归档启用时生效。
/// </summary>
internal sealed class ConversationArchiveMigrationService : BackgroundService
{
    private readonly ConversationStoreOptions _options;
    private readonly ILogger<ConversationArchiveMigrationService> _logger;

    public ConversationArchiveMigrationService(
        IOptions<ConversationStoreOptions> options,
        ILogger<ConversationArchiveMigrationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableColdArchive || _options.ColdArchiveProvider != "SqlServer")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ColdArchiveConnectionString))
        {
            ConversationLog.MigrationSkippedNoConnectionString(_logger);
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.ArchiveMigrationIntervalMinutes);

        ConversationLog.MigrationStarted(_logger, _options.ArchiveMigrationIntervalMinutes, _options.MessageRetentionDays);

        // 启动后等待一个周期再首次执行，避免与启动初始化竞争
        await Task.Delay(interval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MigrateBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                ConversationLog.MigrationBatchFailed(_logger, ex);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

    }

    private async Task MigrateBatchAsync(CancellationToken cancellationToken)
    {
        var batchSize = _options.ArchiveMigrationBatchSize;
        var retentionDays = _options.MessageRetentionDays;

        await using var conn = new SqlConnection(_options.ColdArchiveConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Acquire a session-level application lock to prevent multiple Engine instances
        // from running migration concurrently. Only one instance proceeds; others skip.
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.CommandText = @"
                DECLARE @result INT;
                EXEC @result = sp_getapplock
                    @Resource = 'OpenAgent_ArchiveMigration',
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = 0;
                SELECT @result;";
            var lockResult = (int?)await lockCmd.ExecuteScalarAsync(cancellationToken);

            // sp_getapplock returns 0 or 1 on success, >0 on failure (timeout/conflict)
            if (lockResult is null or > 1)
            {
                return;
            }
        }

        try
        {
            // 1. 扫描超期且有消息的会话
            var candidateIds = new List<string>();
            await using (var scanCmd = conn.CreateCommand())
            {
                scanCmd.CommandText = @"
                    SELECT TOP (@BatchSize) r.ConversationId
                    FROM ConversationRecords r
                    WHERE r.ArchivedAt < DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME())
                      AND EXISTS (SELECT 1 FROM ConversationMessages m WHERE m.ConversationId = r.ConversationId)";
                scanCmd.Parameters.AddWithValue("@BatchSize", batchSize);
                scanCmd.Parameters.AddWithValue("@RetentionDays", retentionDays);

                await using var reader = await scanCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidateIds.Add(reader.GetString(0));
                }
            }

            if (candidateIds.Count == 0)
            {
                return;
            }

            var migrated = 0;
            foreach (var conversationId in candidateIds)
            {
                try
                {
                    await MigrateSingleConversationAsync(conn, conversationId, cancellationToken);
                    migrated++;
                }
                catch (Exception ex)
                {
                    ConversationLog.MigrationMigrateConversationFailed(_logger, ex, conversationId);
                }
            }

        }
        finally
        {
            // Release the session-level application lock
            try
            {
                await using var releaseCmd = conn.CreateCommand();
                releaseCmd.CommandText = "EXEC sp_releaseapplock @Resource = 'OpenAgent_ArchiveMigration', @LockOwner = 'Session'";
                await releaseCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                ConversationLog.MigrationReleaseLockFailed(_logger, ex);
            }
        }
    }

    private static async Task MigrateSingleConversationAsync(SqlConnection conn, string conversationId, CancellationToken cancellationToken)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // 2. 迁移消息到归档表
            await using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT INTO ConversationMessagesArchive
                        (ConversationId, Sequence, MessageId, Role, Content,
                         ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
                    SELECT ConversationId, Sequence, MessageId, Role, Content,
                           ToolCallId, ToolName, Timestamp, MetadataJson, TenantId
                    FROM ConversationMessages
                    WHERE ConversationId = @ConversationId";
                insertCmd.Parameters.AddWithValue("@ConversationId", conversationId);

                await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 3. 从主表删除已迁移的消息
            await using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = @"
                    DELETE FROM ConversationMessages WHERE ConversationId = @ConversationId";
                deleteCmd.Parameters.AddWithValue("@ConversationId", conversationId);

                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
