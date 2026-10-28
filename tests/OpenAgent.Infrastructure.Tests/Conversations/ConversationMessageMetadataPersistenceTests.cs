using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Infrastructure.Entities;
using StackExchange.Redis;
using Xunit;
using static OpenAgent.Infrastructure.Tests.Conversations.ConversationMessageMetadataJsonTests;

namespace OpenAgent.Infrastructure.Tests.Conversations;

/// <summary>
/// 三存储一致性矩阵：同一条含全部字段 + 未知键的消息，经 EF jsonb（写读）、
/// Redis 热缓存（整记录序列化 round-trip）后必须得到深相等的强类型元数据；
/// EF 与 Redis 的 metadata JSON 载荷必须字节级同形（camelCase），
/// InMemory 直存不经序列化、天然保真（由 Core.Tests 的 InMemory 用例锁定）。
/// </summary>
public sealed class ConversationMessageMetadataPersistenceTests
{
    [Fact]
    public async Task EfCoreStore_WriteThenRead_PersistsNewFormAndDeepEquals()
    {
        ContextFactory factory = new($"metadata-{Guid.NewGuid():N}");
        EfCoreConversationStore store = CreateStore(factory);
        await CreateConversationAsync(store);
        ConversationMessageMetadata metadata = CreateFullMetadata();

        AppendResult append = await store.AppendMessagesAsync(
            "tenant-1", "conversation-1", 1, [CreateMessage(metadata)]);
        Assert.True(append.Success);

        await using OpenAgentDbContext context = await factory.CreateDbContextAsync();
        ConversationMessageEntity entity = Assert.Single(
            context.ConversationMessages.AsNoTracking().ToList());
        Assert.Contains("\"files\":[", entity.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"fileId\":\"file-1\"", entity.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"extensions\":{\"Custom\":\"kept\"}", entity.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Files\"", entity.MetadataJson, StringComparison.Ordinal);

        IReadOnlyList<ConversationMessage> reloaded = await store.GetMessagesAsync(
            "tenant-1", "conversation-1", 10);
        AssertMetadataEqual(metadata, Assert.Single(reloaded).Metadata);
    }

    [Fact]
    public async Task EfCoreStore_LegacyRows_ReadBackAsTypedMetadata()
    {
        ContextFactory factory = new($"metadata-{Guid.NewGuid():N}");
        EfCoreConversationStore store = CreateStore(factory);
        await CreateConversationAsync(store);
        await using (OpenAgentDbContext context = await factory.CreateDbContextAsync())
        {
            context.ConversationMessages.Add(new ConversationMessageEntity
            {
                MessageId = "message-legacy",
                ConversationId = "conversation-1",
                Sequence = 1,
                Role = "assistant",
                Content = string.Empty,
                Timestamp = DateTimeOffset.UtcNow,
                MetadataJson = """
                    {"Files":"[{\"fileId\":\"file-1\",\"fileName\":\"notes.md\",\"mediaType\":\"text/markdown\",\"length\":12,\"objectKey\":\"files/t/f-1\"}]","Reasoning":"thinking","ToolArguments":"{\"a\":1}","ExecutionStatus":"Cancelled","Custom":"kept"}
                    """
            });
            await context.SaveChangesAsync();
        }

        IReadOnlyList<ConversationMessage> messages = await store.GetMessagesAsync(
            "tenant-1", "conversation-1", 10);

        // 旧形态载荷没有 Error 键（该字段随失败原因持久化特性引入）：期望为 null。
        ConversationMessageMetadata expected = CreateFullMetadata();
        expected.Error = null;
        AssertMetadataEqual(expected, Assert.Single(messages).Metadata);
    }

    [Fact]
    public async Task RedisCache_RoundTrip_DeepEqualsAndMatchesEfMetadataPayload()
    {
        ConversationMessageMetadata metadata = CreateFullMetadata();
        ConversationRecord record = new()
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            Version = 2,
            Messages = [CreateMessage(metadata)]
        };
        RedisValue captured = RedisValue.Null;
        Mock<IDatabase> database = new();
        database
            .Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey _, RedisValue value, TimeSpan? _, bool _, When _, CommandFlags _) => captured = value)
            .ReturnsAsync(true);
        database
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => captured);
        Mock<IConnectionMultiplexer> connection = new();
        connection
            .Setup(multiplexer => multiplexer.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        RedisConversationCache cache = new(
            connection.Object, Options.Create(new ConversationCacheOptions()));

        await cache.SetAsync(record);
        ConversationRecord? reloaded = await cache.GetAsync("tenant-1", "conversation-1");

        AssertMetadataEqual(metadata, Assert.Single(reloaded!.Messages).Metadata);

        // EF 写侧（Web options）与 Redis 缓存（camelCase policy）的 metadata 载荷必须同形。
        string redisPayload = JsonDocument.Parse(captured.ToString())
            .RootElement.GetProperty("messages")[0].GetProperty("metadata").GetRawText();
        Assert.Equal(ConversationMessageMetadataJson.Serialize(metadata), redisPayload);
    }

    private static EfCoreConversationStore CreateStore(ContextFactory factory) => new(
        factory,
        new FakeUserContext(),
        NullLogger<EfCoreConversationStore>.Instance);

    private static async Task CreateConversationAsync(EfCoreConversationStore store)
    {
        Assert.True(await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            Version = 1
        }));
    }

    private static ConversationMessage CreateMessage(ConversationMessageMetadata metadata) => new()
    {
        MessageId = "message-1",
        Sequence = 1,
        Role = "assistant",
        Content = "partial",
        ToolCallId = "call-1",
        ToolName = "read_file",
        Timestamp = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero),
        Metadata = metadata
    };

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public string UserId => "user-1";
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private sealed class ContextFactory(string databaseName) : IDbContextFactory<OpenAgentDbContext>
    {
        private readonly DbContextOptions<OpenAgentDbContext> _options =
            new DbContextOptionsBuilder<OpenAgentDbContext>()
                .UseInMemoryDatabase(databaseName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

        public OpenAgentDbContext CreateDbContext() => new(_options);

        public Task<OpenAgentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
