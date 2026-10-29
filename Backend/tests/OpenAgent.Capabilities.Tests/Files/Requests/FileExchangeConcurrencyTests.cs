using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Files;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files.Requests;

public sealed class FileExchangeConcurrencyTests
{
    [Fact]
    public async Task PrepareAsync_ThreeSeparateTurnsShareTheHostTransferLimitAndKeepTheirOwnData()
    {
        IOptions<FileExchangeOptions> options = Options.Create(new FileExchangeOptions { Enabled = true, MaxConcurrentTransfers = 2 });
        IOptions<FileAssetOptions> assets = Options.Create(new FileAssetOptions { Enabled = true });
        using FileTransferGate gate = new(options);
        TaskCompletionSource twoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        int active = 0;
        int peak = 0;
        List<FileExchangeContext> contexts = [];
        List<Task<OpenAgent.Contracts.Runtime.RuntimeFilePreparation>> transfers = [];
        List<RecordingWorkspaceFiles> workspaces = [];
        try
        {
            for (int index = 0; index < 3; index++)
            {
                byte[] data = [(byte)index];
                FileAsset asset = new()
                {
                    FileId = "same-id", FileName = "same.bin", TenantId = "tenant-" + index, OwnerUserId = "user-" + index,
                    MediaType = "application/octet-stream", Length = data.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
                    ObjectKey = "object-" + index, Source = FileAssetSource.UserUpload, State = FileAssetState.Ready, CreatedAt = DateTimeOffset.UtcNow
                };
                Mock<IFileAssetService> files = new();
                files.Setup(value => value.GetReferencedAsync(asset.FileId, It.IsAny<FileAssetScope>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(asset);
                files.Setup(value => value.ReadAsync(asset.FileId, It.IsAny<FileAssetScope>(), It.IsAny<CancellationToken>(), It.IsAny<long?>()))
                    .Returns(async (string _, FileAssetScope scope, CancellationToken token, long? _) =>
                    {
                        Assert.Equal(asset.TenantId, scope.TenantId);
                        Assert.Equal(asset.OwnerUserId, scope.UserId);
                        int simultaneous = Interlocked.Increment(ref active);
                        peak = Math.Max(peak, simultaneous);
                        if (Interlocked.Increment(ref reads) == 2) twoStarted.TrySetResult();
                        try
                        {
                            await release.Task.WaitAsync(token);
                            return new FileAssetContent { Asset = asset, Data = data };
                        }
                        finally
                        {
                            Interlocked.Decrement(ref active);
                        }
                    });
                FileAssetExecutionContext execution = new();
                execution.Set(TurnContexts.Create(asset.TenantId, asset.OwnerUserId));
                FileExchangeContext context = new(files.Object, execution, assets, options, gate);
                contexts.Add(context);
                RecordingWorkspaceFiles workspace = new();
                workspaces.Add(workspace);
                transfers.Add(context.PrepareAsync(workspace, [asset], default));
            }
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref reads));
            Assert.False(transfers[2].IsCompleted);
            release.SetResult();
            await Task.WhenAll(transfers);
            Assert.Equal(2, peak);
            Assert.Equal(3, reads);
            for (int index = 0; index < workspaces.Count; index++)
            {
                Assert.Equal(new[] { (byte)index }, workspaces[index].Inputs["same-id"]);
            }
        }
        finally
        {
            release.TrySetResult();
            foreach (FileExchangeContext context in contexts) await context.DisposeAsync();
        }
    }
}
