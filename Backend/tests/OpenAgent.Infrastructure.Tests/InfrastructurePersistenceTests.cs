using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Contracts.Requests;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

[Trait("Category", "Container")]
public sealed class InfrastructurePersistenceTests : IAsyncLifetime
{
    private PostgreSqlContainer? _database;
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        if (!ContainerTestGuard.Enabled)
        {
            return;
        }

        _database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openagent_test")
            .WithUsername("openagent")
            .WithPassword("openagent")
            .Build();
        await _database.StartAsync().ConfigureAwait(false);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OpenAgentDatabase"] = _database.GetConnectionString()
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new TestCurrentUserContext());
        services.AddOpenAgentInfrastructure(configuration);
        _services = services.BuildServiceProvider(validateScopes: true);
        IDbContextFactory<OpenAgentDbContext> contexts = _services.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync().ConfigureAwait(false);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_services != null)
        {
            await _services.DisposeAsync().ConfigureAwait(false);
        }

        if (_database != null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task ConversationStore_StoresFilesAtConversationAndMessageLevel()
    {
        ContainerTestGuard.RequireEnabled();
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        using IServiceScope scope = services.CreateScope();
        IFileAssetRepository files = services.GetRequiredService<IFileAssetRepository>();
        IConversationStore conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        FileAsset asset = new()
        {
            FileId = "file-001",
            TenantId = "tenant-001",
            OwnerUserId = "user-001",
            FileName = "notes.md",
            MediaType = "text/markdown",
            Length = 8,
            Sha256 = "abc",
            ObjectKey = "files/tenant-001/file-001.md",
            Source = FileAssetSource.UserUpload,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await files.CreateAsync(asset, CancellationToken.None);
        bool created = await conversations.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-001",
            TenantId = "tenant-001",
            UserId = "user-001",
            AgentId = "default"
        }, CancellationToken.None);

        AppendResult appended = await conversations.AppendMessagesAsync(
            "tenant-001",
            "conversation-001",
            1,
            [new ConversationMessage
            {
                MessageId = "message-001",
                Sequence = 1,
                Role = "user",
                Content = "Read this file",
                FileIds = [asset.FileId]
            }],
            CancellationToken.None);
        AppendResult reused = await conversations.AppendMessagesAsync(
            "tenant-001",
            "conversation-001",
            appended.NewVersion,
            [new ConversationMessage
            {
                MessageId = "message-002",
                Sequence = 2,
                Role = "user",
                Content = "Reuse this file",
                FileIds = [asset.FileId]
            }],
            CancellationToken.None);
        ConversationRecord? record = await conversations.GetRecordAsync(
            "tenant-001",
            "conversation-001",
            CancellationToken.None);

        Assert.True(created);
        Assert.True(appended.Success);
        Assert.True(reused.Success);
        IReadOnlyList<ConversationMessage> messages = Assert.IsType<ConversationRecord>(record).Messages;
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Equal([asset.FileId], message.FileIds));
    }

    [SkippableFact]
    public async Task FileAssetRepository_EnsuresConversationReferencesConcurrently()
    {
        ContainerTestGuard.RequireEnabled();
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        using IServiceScope scope = services.CreateScope();
        IFileAssetRepository files = services.GetRequiredService<IFileAssetRepository>();
        IConversationStore conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        IDbContextFactory<OpenAgentDbContext> contexts =
            services.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        FileAsset asset = new()
        {
            FileId = "file-concurrent",
            TenantId = "tenant-concurrent",
            OwnerUserId = "user-concurrent",
            FileName = "notes.md",
            MediaType = "text/markdown",
            Length = 8,
            Sha256 = "concurrent-sha",
            ObjectKey = "files/tenant-concurrent/file-concurrent.md",
            Source = FileAssetSource.UserUpload,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await files.CreateAsync(asset, CancellationToken.None);
        Assert.True(await conversations.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-concurrent",
            TenantId = "tenant-concurrent",
            UserId = "user-concurrent",
            AgentId = "default"
        }, CancellationToken.None));

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        Task[] attempts = Enumerable.Range(0, 8)
            .Select(_ => files.EnsureConversationReferencesAsync(
                "conversation-concurrent",
                [asset.FileId],
                createdAt,
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(attempts);

        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync();
        int referenceCount = await context.ConversationFileReferences.CountAsync(item =>
            item.ConversationId == "conversation-concurrent" && item.FileId == asset.FileId);
        Assert.Equal(1, referenceCount);
    }

    [SkippableFact]
    public async Task SkillDefinitionRepository_PersistsTenantScopedObjectStorageSkill()
    {
        ContainerTestGuard.RequireEnabled();
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        ISkillDefinitionRepository repository = services.GetRequiredService<ISkillDefinitionRepository>();
        var package = new SkillInstanceConfig
        {
            TenantId = "tenant-skill",
            Id = "lookup",
            Name = "lookup",
            Type = SkillTypes.AgentSkill,
            SourceType = SkillSourceTypes.ObjectStorage,
            ObjectKey = "private/tenants/example/skill-packages/skill.json"
        };
        await repository.UpsertAsync(package);

        SkillInstanceConfig? storedPackage = await repository.GetAsync("tenant-skill", "lookup");
        IReadOnlyList<SkillInstanceConfig> stored = await repository.ListAsync("tenant-skill");
        SkillInstanceConfig? foreign = await repository.GetAsync("another-tenant", "lookup");

        Assert.Equal(SkillSourceTypes.ObjectStorage, storedPackage?.SourceType);
        Assert.Equal("private/tenants/example/skill-packages/skill.json", storedPackage?.ObjectKey);
        Assert.Single(stored);
        Assert.Null(foreign);
    }

    [SkippableFact]
    public async Task ConversationStore_TokenUsage_RoundTripsProviderCounts()
    {
        ContainerTestGuard.RequireEnabled();
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        using IServiceScope scope = services.CreateScope();
        IConversationStore conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        Assert.True(await conversations.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-usage",
            TenantId = "tenant-usage",
            UserId = "user-usage",
            AgentId = "default"
        }, CancellationToken.None));
        TokenUsage usage = new()
        {
            PromptTokens = 21,
            CompletionTokens = 8,
            TotalTokens = 29,
            CachedInputTokens = 5,
            ReasoningTokens = 3
        };

        AppendResult appended = await conversations.AppendMessagesAsync(
            "tenant-usage",
            "conversation-usage",
            1,
            [new ConversationMessage
            {
                MessageId = "message-usage",
                Sequence = 1,
                Role = "assistant",
                Content = "response",
                TokenUsage = usage,
                ModelId = "provider-model"
            }],
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await conversations.GetRecordAsync(
                "tenant-usage",
                "conversation-usage",
                CancellationToken.None));
        ConversationMessage message = Assert.Single(record.Messages);

        Assert.True(appended.Success);
        Assert.Equal(29, message.TokenUsage?.TotalTokens);
        Assert.Equal(5, message.TokenUsage?.CachedInputTokens);
        Assert.Equal(3, message.TokenUsage?.ReasoningTokens);
        Assert.Equal("provider-model", message.ModelId);
    }

    [SkippableFact]
    public async Task ConversationStore_CompressionAudit_RoundTripsWithoutChangingMessages()
    {
        ContainerTestGuard.RequireEnabled();
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        using IServiceScope scope = services.CreateScope();
        IConversationStore conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        Assert.True(await conversations.CreateAsync(new ConversationRecord
        {
            ConversationId = "conversation-compaction",
            TenantId = "tenant-compaction",
            UserId = "user-compaction",
            AgentId = "default"
        }));
        AppendResult appended = await conversations.AppendMessagesAsync(
            "tenant-compaction",
            "conversation-compaction",
            1,
            [new ConversationMessage
            {
                MessageId = "message-compaction",
                Sequence = 1,
                Role = "user",
                Content = "Retain this audit message"
            }]);
        var summary = new ContextSummary
        {
            CompressionId = "compression-1",
            Strategy = "summarization",
            Trigger = "Automatic",
            Status = "Succeeded",
            Summary = "Retained user intent.",
            OriginalStartSequence = 1,
            OriginalEndSequence = 1,
            CompressedMessageCount = 1,
            LastCompressedAt = DateTimeOffset.UtcNow,
            SourceEndSequence = 1,
            CompactedMessages = [new ConversationMessage
            {
                MessageId = "compacted-message-1",
                Sequence = 1,
                Role = "summary",
                Content = "Retained user intent."
            }]
        };

        bool recorded = await conversations.RecordCompressionAsync(
            "tenant-compaction",
            "conversation-compaction",
            summary);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await conversations.GetRecordAsync("tenant-compaction", "conversation-compaction"));

        Assert.True(recorded);
        Assert.True(appended.Success);
        ContextSummary storedSummary = Assert.Single(record.ContextSummaries);
        Assert.Equal("Retained user intent.", storedSummary.Summary);
        Assert.Equal("Retained user intent.", Assert.Single(storedSummary.CompactedMessages).Content);
        Assert.Equal("Retain this audit message", Assert.Single(record.Messages).Content);
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public string UserId => "test-user";

        public string? TenantId => "tenant-001";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => [];

        public bool IsInRole(string role) => false;
    }
}
