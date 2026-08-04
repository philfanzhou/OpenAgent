using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation.Repository;
using Moq;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class ConversationQueryServiceTests
{
    private static ConversationRecord CreateRecord(string conversationId, string tenantId, string userId = "user-1") => new()
    {
        ConversationId = conversationId,
        TenantId = tenantId,
        UserId = userId,
        LastMessageAt = DateTimeOffset.UtcNow
    };

    private static ConversationQueryService CreateService(
        Mock<IConversationStore> storeMock,
        Mock<IConversationRepository>? coldArchiveMock = null)
    {
        return new ConversationQueryService(
            storeMock.Object,
            NullLogger<ConversationQueryService>.Instance,
            coldArchiveMock?.Object);
    }

    // --- ListConversationsAsync ---

    [Fact]
    public async Task ListConversationsAsync_NoColdArchive_ReturnsHotResultsOnly()
    {
        var storeMock = new Mock<IConversationStore>();
        var hotResults = new List<ConversationRecord>
        {
            CreateRecord("conv-1", "tenant-1"),
            CreateRecord("conv-2", "tenant-1")
        }.AsReadOnly();

        storeMock.Setup(s => s.ListConversationsAsync("tenant-1", 0, 10, default))
            .ReturnsAsync(hotResults);

        var service = CreateService(storeMock);
        var result = await service.ListConversationsAsync("tenant-1", 0, 10);

        Assert.Equal(2, result.Count);
        Assert.Equal("conv-1", result[0].ConversationId);
        Assert.Equal("conv-2", result[1].ConversationId);
    }

    [Fact]
    public async Task ListConversationsAsync_WithColdArchive_MergesAndDeduplicates()
    {
        var storeMock = new Mock<IConversationStore>();
        var coldMock = new Mock<IConversationRepository>();

        var now = DateTimeOffset.UtcNow;
        var hotResults = new List<ConversationRecord>
        {
            new() { ConversationId = "conv-1", TenantId = "tenant-1", UserId = "user-1", LastMessageAt = now },
            new() { ConversationId = "conv-2", TenantId = "tenant-1", UserId = "user-1", LastMessageAt = now.AddMinutes(-5) }
        }.AsReadOnly();

        // conv-2 exists in both hot and cold; conv-3 is cold-only
        var coldResults = new List<ConversationRecord>
        {
            new() { ConversationId = "conv-2", TenantId = "tenant-1", UserId = "user-cold", LastMessageAt = now.AddMinutes(-3) },
            new() { ConversationId = "conv-3", TenantId = "tenant-1", UserId = "user-1", LastMessageAt = now.AddMinutes(-10) }
        }.AsReadOnly();

        storeMock.Setup(s => s.ListConversationsAsync("tenant-1", 0, 10, default))
            .ReturnsAsync(hotResults);
        coldMock.Setup(c => c.ListConversationsAsync("tenant-1", 0, 10, default))
            .ReturnsAsync(coldResults);

        var service = CreateService(storeMock, coldMock);
        var result = await service.ListConversationsAsync("tenant-1", 0, 10);

        // Deduplicated: conv-1, conv-2 (hot wins), conv-3
        Assert.Equal(3, result.Count);
        // Sorted by LastMessageAt desc
        Assert.Equal("conv-1", result[0].ConversationId);
        Assert.Equal("conv-2", result[1].ConversationId);
        Assert.Equal("conv-3", result[2].ConversationId);
        // Hot version of conv-2 wins (UserId = "user-1", not "user-cold")
        Assert.Equal("user-1", result[1].UserId);
    }

    [Fact]
    public async Task ListConversationsAsync_WithColdArchive_FetchesEnoughCandidatesBeforePaging()
    {
        var storeMock = new Mock<IConversationStore>(MockBehavior.Strict);
        var coldMock = new Mock<IConversationRepository>(MockBehavior.Strict);

        var now = DateTimeOffset.UtcNow;
        var hotResults = Enumerable.Range(1, 20)
            .Select(i => new ConversationRecord
            {
                ConversationId = $"hot-{i}",
                TenantId = "tenant-1",
                UserId = "user-1",
                LastMessageAt = now.AddMinutes(-i)
            })
            .ToList()
            .AsReadOnly();
        var coldResults = new List<ConversationRecord>
        {
            new()
            {
                ConversationId = "cold-archived",
                TenantId = "tenant-1",
                UserId = "user-1",
                LastMessageAt = now.AddMinutes(-30)
            }
        }.AsReadOnly();

        storeMock.Setup(s => s.ListConversationsAsync("tenant-1", 0, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hotResults);
        coldMock.Setup(c => c.ListConversationsAsync("tenant-1", 0, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(coldResults);

        var service = CreateService(storeMock, coldMock);
        var result = await service.ListConversationsAsync("tenant-1", 20, 20);

        Assert.Single(result);
        Assert.Equal("cold-archived", result[0].ConversationId);
        storeMock.VerifyAll();
        coldMock.VerifyAll();
    }

    [Fact]
    public async Task ListConversationsAsync_WithRealSqliteColdArchive_ReturnsColdPageWhenHotPageIsExhausted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"query_service_cold_{Guid.NewGuid():N}.db");
        var coldArchive = new SqliteConversationRepository(
            Options.Create(new ConversationStoreOptions
            {
                EnableColdArchive = true,
                ColdArchiveConnectionString = $"Data Source={dbPath}",
                ColdArchiveProvider = "Sqlite"
            }),
            NullLogger<SqliteConversationRepository>.Instance,
            new ConversationStoreMetrics());

        try
        {
            var now = DateTimeOffset.UtcNow;
            var hotStore = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
            for (var i = 1; i <= 20; i++)
            {
                await hotStore.CreateAsync(new ConversationRecord
                {
                    ConversationId = $"hot-{i}",
                    TenantId = "tenant-1",
                    UserId = "user-1",
                    LastMessageAt = now.AddMinutes(-i)
                });
            }

            await coldArchive.ArchiveAsync(new ConversationRecord
            {
                ConversationId = "cold-archived",
                TenantId = "tenant-1",
                UserId = "user-1",
                LastMessageAt = now.AddMinutes(-30)
            });

            var service = new ConversationQueryService(
                hotStore,
                NullLogger<ConversationQueryService>.Instance,
                coldArchive);

            var result = await service.ListConversationsAsync("tenant-1", 20, 20);

            Assert.Single(result);
            Assert.Equal("cold-archived", result[0].ConversationId);
        }
        finally
        {
            coldArchive.Dispose();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task ListConversationsAsync_ColdArchiveFails_ReturnsHotResults()
    {
        var storeMock = new Mock<IConversationStore>();
        var coldMock = new Mock<IConversationRepository>();

        var hotResults = new List<ConversationRecord>
        {
            CreateRecord("conv-1", "tenant-1")
        }.AsReadOnly();

        storeMock.Setup(s => s.ListConversationsAsync("tenant-1", 0, 10, default))
            .ReturnsAsync(hotResults);
        coldMock.Setup(c => c.ListConversationsAsync("tenant-1", 0, 10, default))
            .ThrowsAsync(new Exception("cold archive down"));

        var service = CreateService(storeMock, coldMock);
        var result = await service.ListConversationsAsync("tenant-1", 0, 10);

        Assert.Single(result);
        Assert.Equal("conv-1", result[0].ConversationId);
    }

    // --- SearchConversationsAsync ---

    [Fact]
    public async Task SearchConversationsAsync_NoColdArchive_ReturnsHotResultsOnly()
    {
        var storeMock = new Mock<IConversationStore>();
        var hotResults = new List<ConversationRecord>
        {
            CreateRecord("conv-1", "tenant-1")
        }.AsReadOnly();

        storeMock.Setup(s => s.SearchConversationsAsync("tenant-1", "hello", 0, 10, default))
            .ReturnsAsync(hotResults);

        var service = CreateService(storeMock);
        var result = await service.SearchConversationsAsync("tenant-1", "hello", 0, 10);

        Assert.Single(result);
        Assert.Equal("conv-1", result[0].ConversationId);
    }

    [Fact]
    public async Task SearchConversationsAsync_WithColdArchive_MergesAndDeduplicates()
    {
        var storeMock = new Mock<IConversationStore>();
        var coldMock = new Mock<IConversationRepository>();

        var now = DateTimeOffset.UtcNow;
        var hotResults = new List<ConversationRecord>
        {
            new() { ConversationId = "conv-1", TenantId = "tenant-1", UserId = "user-1", LastMessageAt = now.AddMinutes(-1) },
            new() { ConversationId = "conv-2", TenantId = "tenant-1", UserId = "user-1", LastMessageAt = now.AddMinutes(-5) }
        }.AsReadOnly();

        // conv-2 is in both; conv-3 is cold-only
        var coldResults = new List<ConversationRecord>
        {
            new() { ConversationId = "conv-2", TenantId = "tenant-1", UserId = "user-cold", LastMessageAt = now.AddMinutes(-3) },
            new() { ConversationId = "conv-3", TenantId = "tenant-1", UserId = "user-1", LastMessageAt = now.AddMinutes(-10) }
        }.AsReadOnly();

        storeMock.Setup(s => s.SearchConversationsAsync("tenant-1", "test", 0, 10, default))
            .ReturnsAsync(hotResults);
        coldMock.Setup(c => c.SearchConversationsAsync("tenant-1", "test", 0, 10, default))
            .ReturnsAsync(coldResults);

        var service = CreateService(storeMock, coldMock);
        var result = await service.SearchConversationsAsync("tenant-1", "test", 0, 10);

        Assert.Equal(3, result.Count);
        // Sorted by LastMessageAt desc: conv-1, conv-2, conv-3
        Assert.Equal("conv-1", result[0].ConversationId);
        Assert.Equal("conv-2", result[1].ConversationId);
        Assert.Equal("conv-3", result[2].ConversationId);
        // Hot version of conv-2 wins
        Assert.Equal("user-1", result[1].UserId);
    }

    [Fact]
    public async Task SearchConversationsAsync_WithColdArchive_FetchesEnoughCandidatesBeforePaging()
    {
        var storeMock = new Mock<IConversationStore>(MockBehavior.Strict);
        var coldMock = new Mock<IConversationRepository>(MockBehavior.Strict);

        var now = DateTimeOffset.UtcNow;
        var hotResults = Enumerable.Range(1, 20)
            .Select(i => new ConversationRecord
            {
                ConversationId = $"hot-search-{i}",
                TenantId = "tenant-1",
                UserId = "user-1",
                LastMessageAt = now.AddMinutes(-i)
            })
            .ToList()
            .AsReadOnly();
        var coldResults = new List<ConversationRecord>
        {
            new()
            {
                ConversationId = "cold-search-archived",
                TenantId = "tenant-1",
                UserId = "user-1",
                LastMessageAt = now.AddMinutes(-30)
            }
        }.AsReadOnly();

        storeMock.Setup(s => s.SearchConversationsAsync("tenant-1", "invoice", 0, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hotResults);
        coldMock.Setup(c => c.SearchConversationsAsync("tenant-1", "invoice", 0, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(coldResults);

        var service = CreateService(storeMock, coldMock);
        var result = await service.SearchConversationsAsync("tenant-1", "invoice", 20, 20);

        Assert.Single(result);
        Assert.Equal("cold-search-archived", result[0].ConversationId);
        storeMock.VerifyAll();
        coldMock.VerifyAll();
    }

    [Fact]
    public async Task SearchConversationsAsync_ColdArchiveFails_ReturnsHotResults()
    {
        var storeMock = new Mock<IConversationStore>();
        var coldMock = new Mock<IConversationRepository>();

        var hotResults = new List<ConversationRecord>
        {
            CreateRecord("conv-1", "tenant-1")
        }.AsReadOnly();

        storeMock.Setup(s => s.SearchConversationsAsync("tenant-1", "test", 0, 10, default))
            .ReturnsAsync(hotResults);
        coldMock.Setup(c => c.SearchConversationsAsync("tenant-1", "test", 0, 10, default))
            .ThrowsAsync(new Exception("cold archive down"));

        var service = CreateService(storeMock, coldMock);
        var result = await service.SearchConversationsAsync("tenant-1", "test", 0, 10);

        Assert.Single(result);
        Assert.Equal("conv-1", result[0].ConversationId);
    }
}
