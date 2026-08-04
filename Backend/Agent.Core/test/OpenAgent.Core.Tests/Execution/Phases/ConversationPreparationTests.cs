using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Engine;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Conversation.Compression;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Execution.Persistence;
using OpenAgent.Core.Execution.Phases;
using OpenAgent.Core.Execution.Tools;
using OpenAgent.Core.Impl.Compression;
using OpenAgent.Core.Observability;
using Xunit;

namespace OpenAgent.Core.Tests.Execution.Phases;

public class ConversationPreparationTests
{
    [Fact]
    public async Task PrepareAsync_LoadThrows_DisposesLockAndRethrows()
    {
        // Arrange
        var conversationLock = new TrackingConversationLock();
        var preparation = CreatePreparation(conversationLock, new ThrowingConversationStore());

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(
            "hello",
            CreateIdentity(),
            CreateTools(),
            new Dictionary<string, object>(),
            NullLogger.Instance,
            CancellationToken.None));

        // Assert
        Assert.Equal("store failure", exception.Message);
        Assert.True(conversationLock.Handle.Disposed);
    }

    [Fact]
    public async Task PrepareAsync_LockAlreadyHeld_ThrowsConflict()
    {
        // Arrange
        var preparation = CreatePreparation(new BusyConversationLock(), new ThrowingConversationStore());

        // Act
        var exception = await Assert.ThrowsAsync<AgentException>(() => preparation.PrepareAsync(
            "hello",
            CreateIdentity(),
            CreateTools(),
            new Dictionary<string, object>(),
            NullLogger.Instance,
            CancellationToken.None));

        // Assert
        Assert.Equal(AgentErrorCode.Conflict, exception.ErrorCode);
    }

    private static ConversationPreparation CreatePreparation(
        IConversationLock conversationLock,
        IConversationStore store)
    {
        var persister = new PartialMessagePersister(NullLogger<PartialMessagePersister>.Instance);
        var compressor = new ContextCompressorDispatcher(
            Array.Empty<IContextCompressor>(),
            NullLogger<ContextCompressorDispatcher>.Instance,
            new CompressionMetrics());
        var loader = new ConversationLoader(
            store,
            Options.Create(new ConversationStoreOptions { MaxHistoryMessages = 20, EnableColdArchive = false }),
            compressor,
            persister,
            NullLogger<ConversationLoader>.Instance);
        return new ConversationPreparation(conversationLock, loader, persister);
    }

    private static AgentIdentity CreateIdentity()
    {
        return new AgentIdentity(
            "agent-1",
            AgentRunTestFactory.CreateConfig(),
            3,
            new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true },
            new LlmConfig(),
            new ConversationContext("conv-1", "tenant-1", "user-1", "agent-1", null),
            null);
    }

    private static ToolPreparationResult CreateTools()
    {
        return new ToolPreparationResult(
            new ToolAssembly(
                Array.Empty<SkillDescriptor>(),
                Array.Empty<ToolDefinition>(),
                Array.Empty<ToolDefinition>()),
            "system-prompt",
            new AgentExecutionTelemetry("agent-1", "conv-1", "tenant-1", null, false));
    }

    private sealed class TrackingLockHandle : IConversationLockHandle
    {
        public string TenantId => "tenant-1";
        public string ConversationId => "conv-1";
        public string OwnerToken => "owner-token";
        public bool IsHeld => !Disposed;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingConversationLock : IConversationLock
    {
        public TrackingLockHandle Handle { get; } = new();

        public Task<IConversationLockHandle?> TryAcquireAsync(
            string tenantId,
            string conversationId,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IConversationLockHandle?>(Handle);
    }

    private sealed class BusyConversationLock : IConversationLock
    {
        public Task<IConversationLockHandle?> TryAcquireAsync(
            string tenantId,
            string conversationId,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IConversationLockHandle?>(null);
    }

    private sealed class ThrowingConversationStore : IConversationStore
    {
        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string tenantId, string conversationId, int maxMessages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
            string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConversationRecord?> GetRecordAsync(
            string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("store failure");

        public Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AppendResult> AppendMessagesAsync(
            string tenantId, string conversationId, int expectedVersion,
            IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateStatusAsync(
            string tenantId, string conversationId, ConversationStatus status,
            int expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
            string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
            string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SoftDeleteAsync(
            string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
