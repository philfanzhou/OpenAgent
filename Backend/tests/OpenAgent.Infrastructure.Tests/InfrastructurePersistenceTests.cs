using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public sealed class InfrastructurePersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("openagent_test")
        .WithUsername("openagent")
        .WithPassword("openagent")
        .Build();
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
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

        await _database.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ConversationStore_StoresFilesAtConversationAndMessageLevel()
    {
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        IFileAssetRepository files = services.GetRequiredService<IFileAssetRepository>();
        IConversationStore conversations = services.GetRequiredService<IConversationStore>();
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

    [Fact]
    public async Task FileAssetRepository_EnsuresConversationReferencesConcurrently()
    {
        ServiceProvider services = Assert.IsType<ServiceProvider>(_services);
        IFileAssetRepository files = services.GetRequiredService<IFileAssetRepository>();
        IConversationStore conversations = services.GetRequiredService<IConversationStore>();
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

    [Fact]
    public async Task SkillDefinitionRepository_PersistsTenantScopedObjectStorageSkill()
    {
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

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public string UserId => "test-user";

        public string? TenantId => "tenant-001";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => [];

        public bool IsInRole(string role) => false;
    }
}
