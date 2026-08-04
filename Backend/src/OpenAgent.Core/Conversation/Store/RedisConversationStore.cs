using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Conversation.Scripts;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using StackExchange.Redis;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class RedisConversationStore : IConversationStore
{
    private readonly IConnectionMultiplexer _connection;
    private readonly ConversationStoreOptions _options;
    private readonly ILogger<RedisConversationStore> _logger;
    private readonly ConversationStoreMetrics _metrics;
    private readonly RedisTenantIndexManager _tenantIndexManager;

    public RedisConversationStore(
        IConnectionMultiplexer connection,
        IOptions<ConversationStoreOptions> options,
        ILogger<RedisConversationStore> logger,
        ConversationStoreMetrics metrics,
        RedisTenantIndexManager tenantIndexManager)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _tenantIndexManager = tenantIndexManager;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var record = await GetRecordInternalAsync(tenantId, conversationId);
            if (record == null)
            {
                _metrics.RecordMiss();
                return Array.Empty<ConversationMessage>();
            }

            _metrics.RecordHit();
            var messages = record.Messages.TakeLast(maxMessages).ToList().AsReadOnly();
            _metrics.RecordMessagesLoaded(messages.Count);
            return messages;
        }
        catch (Exception ex)
        {
            _metrics.RecordReadFailure();
            ConversationLog.LoadMessagesFailed(_logger, ex, conversationId);
            return Array.Empty<ConversationMessage>();
        }
        finally
        {
            _metrics.RecordReadLatency(sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var record = await GetRecordInternalAsync(tenantId, conversationId);
            if (record == null)
            {
                _metrics.RecordMiss();
                return Array.Empty<ConversationMessage>();
            }

            _metrics.RecordHit();
            var messages = record.Messages.Skip(skip).Take(take).ToList().AsReadOnly();
            _metrics.RecordMessagesLoaded(messages.Count);
            return messages;
        }
        catch (Exception ex)
        {
            _metrics.RecordReadFailure();
            ConversationLog.LoadPagedMessagesFailed(_logger, ex, conversationId);
            return Array.Empty<ConversationMessage>();
        }
        finally
        {
            _metrics.RecordReadLatency(sw.ElapsedMilliseconds);
        }
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await GetRecordInternalAsync(tenantId, conversationId);
        }
        catch (Exception ex)
        {
            _metrics.RecordReadFailure();
            ConversationLog.LoadRecordFailed(_logger, ex, conversationId);
            return null;
        }
        finally
        {
            _metrics.RecordReadLatency(sw.ElapsedMilliseconds);
        }
    }

    public async Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var db = GetDatabase();
            if (db == null) return false;

            var key = BuildKey(record.TenantId, record.ConversationId);
            var json = SerializeRecord(record);

            // 仅在 key 不存在时写入（NX = Not eXists）
            var created = await db.StringSetAsync(key, json, TimeSpan.FromMinutes(_options.RedisTtlMinutes), When.NotExists);
            if (created)
            {
                // 维护 tenant 索引 Set
                await _tenantIndexManager.AddAndRenewAsync(db, record.TenantId, record.ConversationId);

            }
            return created;
        }
        catch (Exception ex)
        {
            _metrics.RecordWriteFailure();
            ConversationLog.CreateRecordFailed(_logger, ex, record.ConversationId);
            return false;
        }
        finally
        {
            _metrics.RecordWriteLatency(sw.ElapsedMilliseconds);
        }
    }

    public async Task<AppendResult> AppendMessagesAsync(
        string tenantId, string conversationId, int expectedVersion,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var db = GetDatabase();
            if (db == null) return AppendResult.Conflict("Redis not available");

            var key = BuildKey(tenantId, conversationId);
            var args = new RedisValue[]
            {
                expectedVersion,
                JsonSerializer.Serialize(messages, JsonOptions),
                (long)TimeSpan.FromMinutes(_options.RedisTtlMinutes).TotalSeconds,
                DateTimeOffset.UtcNow.ToString("O"),
                messages.Count > 0 ? messages[^1].Timestamp.ToString("O") : string.Empty
            };

            var result = await db.ScriptEvaluateAsync(ConversationScripts.AppendMessages, new RedisKey[] { key }, args);
            var resultJson = (string?)result;
            if (string.IsNullOrEmpty(resultJson))
            {
                return AppendResult.Conflict("Redis returned empty script result");
            }

            var parsed = JsonSerializer.Deserialize<LuaAppendResult>(resultJson, JsonOptions);
            if (parsed == null)
            {
                return AppendResult.Conflict("Failed to parse Redis script result");
            }

            var appendResult = parsed.Status switch
            {
                "OK" => AppendResult.Ok(parsed.Version, parsed.Count, parsed.Skipped),
                "NOT_FOUND" => AppendResult.Conflict("Conversation not found"),
                "CONFLICT" => AppendResult.Conflict($"Version conflict: expected {expectedVersion}, actual {parsed.Actual}"),
                _ => AppendResult.Conflict($"Redis error: {parsed.Reason}")
            };

            // Renew tenant index TTL on successful write to keep it in sync with data TTL
            if (appendResult.Success)
            {
                try
                {
                    _ = _tenantIndexManager.RenewAsync(db, tenantId);
                }
                catch (Exception ex)
                {
                    ConversationLog.AppendTenantIndexTtlRenewFailed(_logger, ex, tenantId);
                }
            }

            return appendResult;
        }
        catch (Exception ex)
        {
            _metrics.RecordWriteFailure();
            ConversationLog.AppendMessagesFailed(_logger, ex, conversationId);
            return AppendResult.Conflict($"Redis error ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            _metrics.RecordWriteLatency(sw.ElapsedMilliseconds);
        }
    }

    private sealed class LuaAppendResult
    {
        public string Status { get; set; } = string.Empty;
        public int Version { get; set; }
        public int Count { get; set; }
        public int Skipped { get; set; }
        public int Actual { get; set; }
        public string? Reason { get; set; }
    }

    public async Task<bool> UpdateStatusAsync(
        string tenantId, string conversationId, ConversationStatus status,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var db = GetDatabase();
            if (db == null) return false;

            var key = BuildKey(tenantId, conversationId);
            var record = await GetRecordInternalAsync(tenantId, conversationId);

            if (record == null) return false;
            if (record.Version != expectedVersion) return false;

            record.Status = status;
            record.Version++;
            record.UpdatedAt = DateTimeOffset.UtcNow;

            var json = SerializeRecord(record);
            await db.StringSetAsync(key, json, TimeSpan.FromMinutes(_options.RedisTtlMinutes));

            // Renew tenant index TTL to keep it in sync with data TTL
            try
            {
                _ = _tenantIndexManager.RenewAsync(db, tenantId);
            }
            catch (Exception indexEx)
            {
                ConversationLog.UpdateStatusTenantIndexTtlRenewFailed(_logger, indexEx, tenantId);
            }

            return true;
        }
        catch (Exception ex)
        {
            _metrics.RecordWriteFailure();
            ConversationLog.UpdateStatusFailed(_logger, ex, conversationId);
            return false;
        }
        finally
        {
            _metrics.RecordWriteLatency(sw.ElapsedMilliseconds);
        }
    }

    private async Task<ConversationRecord?> GetRecordInternalAsync(string tenantId, string conversationId)
    {
        var db = GetDatabase();
        if (db == null) return null;

        var key = BuildKey(tenantId, conversationId);
        var json = await db.StringGetAsync(key);

        if (json.IsNullOrEmpty)
        {
            return null;
        }

        return DeserializeRecord(json!);
    }

    private IDatabase? GetDatabase()
    {
        try
        {
            return _connection.GetDatabase();
        }
        catch (Exception ex)
        {
            ConversationLog.GetDatabaseFailed(_logger, ex);
            return null;
        }
    }

    private static string BuildKey(string tenantId, string conversationId) => $"conversation:{tenantId}:{conversationId}";

    private static string SerializeRecord(ConversationRecord record)
    {
        return JsonSerializer.Serialize(record, JsonOptions);
    }

    private static ConversationRecord? DeserializeRecord(string json)
    {
        return JsonSerializer.Deserialize<ConversationRecord>(json, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var db = GetDatabase();
        if (db == null) return Array.Empty<ConversationRecord>();

        try
        {
            var indexKey = RedisTenantIndexManager.BuildTenantIndexKey(tenantId);
            var conversationIds = await db.SetMembersAsync(indexKey);

            if (conversationIds.Length == 0)
            {
                return Array.Empty<ConversationRecord>();
            }

            // Batch fetch all conversation records in a single round-trip
            var keys = conversationIds
                .Select(entry => (RedisKey)BuildKey(tenantId, entry.ToString()))
                .ToArray();
            var values = await db.StringGetAsync(keys);

            var records = new List<ConversationRecord>();
            var expiredEntries = new List<RedisValue>();

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].HasValue)
                {
                    var record = DeserializeRecord(values[i]!);
                    if (record != null && !record.IsDeletedByUser)
                    {
                        records.Add(StripMessages(record));
                    }
                }
                else
                {
                    // Data expired, track for index cleanup
                    expiredEntries.Add(conversationIds[i]);
                }
            }

            // Clean up expired entries from the index set
            if (expiredEntries.Count > 0)
            {
                _ = db.SetRemoveAsync(indexKey, expiredEntries.ToArray());
            }

            return records
                .OrderByDescending(r => r.LastMessageAt)
                .Skip(skip)
                .Take(take)
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            ConversationLog.ListConversationsFailed(_logger, ex, tenantId);
            return Array.Empty<ConversationRecord>();
        }
    }

    public async Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        var db = GetDatabase();
        if (db == null) return Array.Empty<ConversationRecord>();

        try
        {
            var indexKey = RedisTenantIndexManager.BuildTenantIndexKey(tenantId);
            var conversationIds = await db.SetMembersAsync(indexKey);

            if (conversationIds.Length == 0)
            {
                return Array.Empty<ConversationRecord>();
            }

            // Batch fetch all conversation records in a single round-trip
            var keys = conversationIds
                .Select(entry => (RedisKey)BuildKey(tenantId, entry.ToString()))
                .ToArray();
            var values = await db.StringGetAsync(keys);

            var records = new List<ConversationRecord>();

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].HasValue)
                {
                    var record = DeserializeRecord(values[i]!);
                    if (record != null && !record.IsDeletedByUser
                        && (record.Title != null && record.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || record.Messages.Any(m => m.Content != null && m.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
                    {
                        records.Add(StripMessages(record));
                    }
                }
            }

            return records
                .OrderByDescending(r => r.LastMessageAt)
                .Skip(skip)
                .Take(take)
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            ConversationLog.SearchConversationsFailed(_logger, ex, tenantId);
            return Array.Empty<ConversationRecord>();
        }
    }

    public async Task<bool> SoftDeleteAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var db = GetDatabase();
            if (db == null) return false;

            var record = await GetRecordInternalAsync(tenantId, conversationId);
            if (record == null) return false;

            record.IsDeletedByUser = true;
            record.DeletedAt = DateTimeOffset.UtcNow;
            record.UpdatedAt = DateTimeOffset.UtcNow;

            var key = BuildKey(tenantId, conversationId);
            var json = SerializeRecord(record);
            await db.StringSetAsync(key, json, TimeSpan.FromMinutes(_options.RedisTtlMinutes));

            return true;
        }
        catch (Exception ex)
        {
            _metrics.RecordWriteFailure();
            ConversationLog.SoftDeleteFailed(_logger, ex, conversationId);
            return false;
        }
        finally
        {
            _metrics.RecordWriteLatency(sw.ElapsedMilliseconds);
        }
    }

    private static ConversationRecord StripMessages(ConversationRecord record) => new()
    {
        ConversationId = record.ConversationId,
        TenantId = record.TenantId,
        UserId = record.UserId,
        AgentId = record.AgentId,
        TraceId = record.TraceId,
        Version = record.Version,
        Status = record.Status,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        LastMessageAt = record.LastMessageAt,
        MessageCount = record.MessageCount,
        Title = record.Title,
        IsDeletedByUser = record.IsDeletedByUser,
        DeletedAt = record.DeletedAt,
        ArchivedAt = record.ArchivedAt,
        Messages = []
    };
}
