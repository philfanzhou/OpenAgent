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
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DiscoverAsync_RequiresBothAssetStorageAndExchangeEnabled(bool assets, bool exchange)
    {
        await using Harness harness = new(new() { Enabled = exchange }, assets);
        FileExchangeCapabilitySource source = new(harness.Context);
        Assert.Empty(await source.DiscoverAsync("agent", new(), new AgentUserContext { UserId = "user" }, default));
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
            IOptions<FileExchangeOptions> settings = Options.Create(exchange ?? new() { Enabled = true });
            _gate = new(settings);
            Files = new(Repository, Objects, assets);
            FileAssetExecutionContext execution = new();
            execution.Set(TurnContexts.Create());
            Context = new(Files, execution, assets, settings, _gate);
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
