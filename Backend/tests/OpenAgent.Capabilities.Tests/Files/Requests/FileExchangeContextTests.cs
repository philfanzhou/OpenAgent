using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Capabilities.Files.Tools;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files.Requests;

public sealed class FileExchangeContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAsync_RetryAfterStorageOrReferenceFailureRegistersAsset(bool referenceFails)
    {
        await using Harness harness = new();
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        harness.Workspace.Outputs["outputs/file.bin"] = [1, 2];
        AgentException failure = new(AgentErrorCode.DependencyUnavailable, "Storage unavailable.");
        if (referenceFails)
        {
            harness.Repository.ReferenceFailure = failure;
        }
        else
        {
            harness.Objects.WriteFailure = failure;
        }
        await Assert.ThrowsAsync<AgentException>(() => harness.Context.ExportAsync("outputs/file.bin", null, default));
        Assert.Empty(harness.Repository.References);
        harness.Repository.ReferenceFailure = null;
        harness.Objects.WriteFailure = null;
        FileAsset retried = await harness.Context.ExportAsync("outputs/file.bin", null, default);
        Assert.Equal(FileAssetState.Ready, retried.State);
        Assert.Contains("conversation:" + retried.FileId, harness.Repository.References);
        Assert.Equal(referenceFails ? 1 : 2, harness.Repository.Assets.Count);
    }
    [Fact]
    public async Task ExportAsync_StoresBinaryReferencesItAndReusesSameTurnRetry()
    {
        await using Harness harness = new();
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        byte[] data = [0, 255, 128, 10];
        harness.Workspace.Outputs["outputs/result.xlsx"] = data;
        FileAsset first = await harness.Context.ExportAsync("outputs/result.xlsx", null, default);
        FileAsset retried = await harness.Context.ExportAsync("outputs/result.xlsx", null, default);
        Assert.Equal(first.FileId, retried.FileId);
        Assert.Single(harness.Repository.Assets);
        Assert.Equal(data, (await harness.Files.ReadAsync(first.FileId, new() { TenantId = "tenant", UserId = "user", ConversationId = "conversation" }, default)).Data);
        Assert.Contains("conversation:" + first.FileId, harness.Repository.References);
        harness.Workspace.Outputs["outputs/result.xlsx"] = [1, 2, 3];
        FileAsset revised = await harness.Context.ExportAsync("outputs/result.xlsx", null, default);
        Assert.NotEqual(first.FileId, revised.FileId);
        Assert.Equal(FileAssetSource.Agent, revised.Source);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(10, 3)]
    public async Task ExportAsync_LimitsDistinctSuccessfulExportsAndAllowsCachedRetry(int maxFiles, long maxBytes)
    {
        await using Harness harness = new(new() { Enabled = true, MaxOutputFiles = maxFiles, MaxOutputBytes = maxBytes });
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        harness.Workspace.Outputs["outputs/first.bin"] = [1, 2];
        harness.Workspace.Outputs["outputs/second.bin"] = [3, 4];
        FileAsset first = await harness.Context.ExportAsync("outputs/first.bin", null, default);
        await Assert.ThrowsAsync<AgentException>(() => harness.Context.ExportAsync("outputs/second.bin", null, default));
        Assert.Equal(first.FileId, (await harness.Context.ExportAsync("outputs/first.bin", null, default)).FileId);
        Assert.Single(harness.Repository.Assets);
    }

    [Fact]
    public async Task SaveOutputsAsync_SkipsEmptyAndOversizedFilesAndSavesTheRest()
    {
        await using Harness harness = new(new() { Enabled = true, MaxOutputBytes = 3 });
        RuntimeFilePreparation prepared = await harness.Context.PrepareAsync(harness.Workspace, [], default);
        harness.Workspace.Outputs["outputs/empty.bin"] = [];
        harness.Workspace.Outputs["outputs/too-large.bin"] = [1, 2, 3, 4];
        harness.Workspace.Outputs["outputs/valid.bin"] = [1, 2, 3];
        harness.Workspace.Outputs["outputs/past-limit.bin"] = [1];

        await prepared.SaveOutputsAsync!(default);

        Assert.Equal("valid.bin", Assert.Single(harness.Repository.Assets.Values).FileName);
        Assert.Single(harness.Repository.References);
    }

    [Fact]
    public async Task SaveOutputsAsync_StorageFailure_LeavesOutputUnpublishedAndAllowsExplicitRetry()
    {
        await using Harness harness = new();
        RuntimeFilePreparation prepared = await harness.Context.PrepareAsync(harness.Workspace, [], default);
        const string path = "outputs/result.bin";
        harness.Workspace.Outputs[path] = [1, 2];
        harness.Objects.WriteFailure = new AgentException(AgentErrorCode.DependencyUnavailable, "Storage unavailable.");

        await prepared.SaveOutputsAsync!(default);

        Assert.Empty(harness.Repository.References);
        AgentException failed = await Assert.ThrowsAsync<AgentException>(() => harness.Context.ExportAsync(path, null, default));
        Assert.Equal(AgentErrorCode.DependencyUnavailable, failed.ErrorCode);
        harness.Objects.WriteFailure = null;
        FileAsset retried = await harness.Context.ExportAsync(path, null, default);
        Assert.Equal(FileAssetState.Ready, retried.State);
        Assert.Contains("conversation:" + retried.FileId, harness.Repository.References);
    }

    [Fact]
    public async Task SaveOutputsAsync_StopsAtItsOwnDeadlineAndPropagatesCallerCancellation()
    {
        await using Harness harness = new(new() { Enabled = true, AutoSaveTimeoutMs = 100 });
        RuntimeFilePreparation prepared = await harness.Context.PrepareAsync(harness.Workspace, [], default);
        harness.Workspace.Outputs["outputs/result.bin"] = [1, 2];
        harness.Workspace.BeforeSnapshot = token => Task.Delay(Timeout.Infinite, token);

        await prepared.SaveOutputsAsync!(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(harness.Repository.References);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.SaveOutputsAsync(cancelled.Token));
    }

    [Fact]
    public async Task PrepareAsync_SkipsCorruptOutputAndAllowsReplacingItWithIdenticalContent()
    {
        await using Harness harness = new();
        FileAssetScope scope = new() { TenantId = "tenant", UserId = "user", ConversationId = "conversation" };
        byte[] bytes = [1, 2];
        const string path = "outputs/result.bin";
        using MemoryStream stream = new(bytes);
        FileAsset damaged = await harness.Files.UploadAsync(new()
        {
            FileName = "result.bin", Source = FileAssetSource.Agent, RuntimeOutputPath = path
        }, stream, scope, default);
        await harness.Files.EnsureReferencesAsync([damaged.FileId], scope, default);
        harness.Objects.ContentsByKey[damaged.ObjectKey] = [9, 9];

        RuntimeFilePreparation prepared = await harness.Context.PrepareAsync(harness.Workspace, [], default);
        Assert.Empty(prepared.Outputs);
        Assert.Empty(harness.Workspace.Outputs);

        harness.Workspace.Outputs[path] = bytes;
        FileAsset replaced = await harness.Context.ExportAsync(path, null, default);

        Assert.NotEqual(damaged.FileId, replaced.FileId);
        Assert.Equal(damaged.Sha256, replaced.Sha256);
    }

    [Fact]
    public async Task PrepareAsync_LimitsColdRestoreCandidatesBeforeReadingObjects()
    {
        await using Harness harness = new(new() { Enabled = true, MaxOutputFiles = 2 });
        FileAssetScope scope = new() { TenantId = "tenant", UserId = "user", ConversationId = "conversation" };
        for (int i = 0; i < 25; i++)
        {
            using MemoryStream content = new(new byte[] { (byte)i });
            FileAsset output = await harness.Files.UploadAsync(new()
            {
                FileName = $"file-{i}.bin", Source = FileAssetSource.Agent, RuntimeOutputPath = $"outputs/file-{i}.bin"
            }, content, scope, default);
            await harness.Files.EnsureReferencesAsync([output.FileId], scope, default);
        }

        RuntimeFilePreparation prepared = await harness.Context.PrepareAsync(harness.Workspace, [], default);

        Assert.Equal(8, harness.Repository.LastOutputCandidateLimit);
        Assert.Equal(2, prepared.Outputs.Count);
        Assert.Equal(2, harness.Objects.ReadCount);
        Assert.Equal(25, harness.Repository.References.Count);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsInFlightExportWithoutRegisteringIt()
    {
        await using Harness harness = new();
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Workspace.BeforeSnapshot = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        Task<FileAsset> exporting = harness.Context.ExportAsync("outputs/file.bin", null, default);
        await started.Task;
        await harness.Context.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporting);
        Assert.Empty(harness.Repository.Assets);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Context.ExportAsync("outputs/file.bin", null, default));
    }

    [Theory]
    [InlineData("../leak")]
    [InlineData("bad\\name")]
    [InlineData("bad\nname")]
    public async Task ExportAsync_RejectsUnsafeSuggestedName(string name)
    {
        await using Harness harness = new();
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        harness.Workspace.Outputs["outputs/file.bin"] = [1, 2];
        await Assert.ThrowsAsync<AgentException>(() => harness.Context.ExportAsync("outputs/file.bin", name, default));
        Assert.Empty(harness.Repository.Assets);
    }
    [Fact]
    public async Task ExportAsync_RejectsNamesTooLongForAssetStorage()
    {
        await using Harness harness = new();
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        harness.Workspace.Outputs["outputs/file.bin"] = [1, 2];

        AgentException error = await Assert.ThrowsAsync<AgentException>(() =>
            harness.Context.ExportAsync("outputs/file.bin", new string('a', FileAsset.MaxFileNameLength + 1), default));

        Assert.Equal(AgentErrorCode.InvalidRequest, error.ErrorCode);
        Assert.Contains(FileAsset.MaxFileNameLength.ToString(), error.Message);
        Assert.Empty(harness.Repository.Assets);
    }

    [Fact]
    public async Task PrepareAsync_MaterializesOriginalAndAllowsHistoryImportUntilLeaseEnds()
    {
        await using Harness harness = new();
        FileAsset original = await harness.AddAsync("同名.xlsx", [0, 255, 10]);
        FileAsset historical = await harness.AddAsync("同名.xlsx", [1, 128, 0]);
        RuntimeFilePreparation preparation = await harness.Context.PrepareAsync(harness.Workspace, [original], default);
        Assert.Equal(original.Sha256, Assert.Single(preparation.Files).File.Sha256);
        Assert.Equal(new byte[] { 0, 255, 10 }, harness.Workspace.Inputs[original.FileId]);
        Assert.False(harness.Workspace.Inputs.ContainsKey(historical.FileId));
        await harness.Context.ImportAsync(historical.FileId, default);
        Assert.Equal(new byte[] { 1, 128, 0 }, harness.Workspace.Inputs[historical.FileId]);
        await preparation.Lease.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Context.ImportAsync(original.FileId, default));
    }

    [Theory]
    [InlineData("tenant", "other-user", true)]
    [InlineData("other-tenant", "user", true)]
    [InlineData("tenant", "user", false)]
    public async Task ImportAsync_RejectsWrongOwnerTenantOrMissingConversationReference(string tenant, string user, bool referenced)
    {
        await using Harness harness = new();
        FileAsset asset = await harness.AddAsync("file.bin", [1], new() { TenantId = tenant, UserId = user });
        if (referenced)
        {
            harness.Repository.References.Add("conversation:" + asset.FileId);
        }
        await harness.Context.PrepareAsync(harness.Workspace, [], default);
        await Assert.ThrowsAsync<AgentException>(() => harness.Context.ImportAsync(asset.FileId, default));
        Assert.Empty(harness.Workspace.Inputs);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(10, 3)]
    public async Task ImportAsync_CountsOnlyDistinctSuccessfulInputs(int maxFiles, long maxBytes)
    {
        await using Harness harness = new(new() { Enabled = true, MaxInputFiles = maxFiles, MaxInputBytes = maxBytes });
        FileAsset first = await harness.AddAsync("first.bin", [1, 2]);
        FileAsset second = await harness.AddAsync("second.bin", [3, 4]);
        await harness.Context.PrepareAsync(harness.Workspace, [first], default);
        await harness.Context.ImportAsync(first.FileId, default);
        await Assert.ThrowsAsync<AgentException>(() => harness.Context.ImportAsync(second.FileId, default));
        Assert.Single(harness.Workspace.Inputs);
    }

    [Fact]
    public async Task PrepareAsync_ReadFailureStopsPreparationAndInvalidatesLease()
    {
        await using Harness harness = new();
        FileAsset asset = await harness.AddAsync("file.bin", [1, 2]);
        harness.Objects.ContentsByKey[asset.ObjectKey] = [9, 9];
        await Assert.ThrowsAsync<AgentException>(() => harness.Context.PrepareAsync(harness.Workspace, [asset], default));
        Assert.Empty(harness.Workspace.Inputs);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Context.ImportAsync(asset.FileId, default));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(true, null, true)]
    public async Task DiscoverAsync_UsesAssetStorageAndOptionalExchangeOverride(bool assets, bool? exchange, bool available)
    {
        await using Harness harness = new(exchange is bool enabled ? new() { Enabled = enabled } : null, assets);
        FileExchangeCapabilitySource source = new(harness.Context);
        IReadOnlyList<CapabilityDefinition> tools = await source.DiscoverAsync("agent", new(),
            new AgentUserContext { UserId = "user" }, default);
        Assert.Equal(available, tools.Count > 0);
    }

    internal sealed class Harness : IAsyncDisposable
    {
        internal RecordingFileAssetRepository Repository { get; } = new();
        internal RecordingFileObjectStore Objects { get; } = new();
        internal RecordingWorkspaceFiles Workspace { get; } = new();
        internal FileAssetService Files { get; }
        internal FileExchangeContext Context { get; }
        private readonly FileTransferGate _gate;

        internal Harness(FileExchangeOptions? exchange = null, bool assetsEnabled = true)
        {
            IOptions<FileAssetOptions> assets = Options.Create(new FileAssetOptions { Enabled = assetsEnabled });
            IOptions<FileExchangeOptions> settings = Options.Create(exchange ?? new());
            _gate = new(settings);
            Files = new(Repository, Objects, assets);
            FileAssetExecutionContext execution = new();
            execution.Set(TurnContexts.Create());
            Context = new(Files, execution, assets, settings, _gate, NullLogger<FileExchangeContext>.Instance);
        }

        internal async Task<FileAsset> AddAsync(string name, byte[] data, FileAssetScope? scope = null)
        {
            await using MemoryStream stream = new(data);
            FileAsset asset = await Files.UploadAsync(new() { FileName = name, Source = FileAssetSource.UserUpload }, stream,
                scope ?? new() { TenantId = "tenant", UserId = "user", ConversationId = "conversation" }, default);
            if (scope is null)
            {
                Repository.References.Add("conversation:" + asset.FileId);
            }
            return asset;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            _gate.Dispose();
        }
    }
}
