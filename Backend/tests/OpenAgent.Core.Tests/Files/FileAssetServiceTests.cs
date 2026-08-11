using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public class FileAssetServiceTests
{
    [Fact]
    public async Task UploadAsync_StoresAssetAndConversationReference()
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("# Hello"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = "notes.md",
                MediaType = "text/markdown",
                Source = FileAssetSource.UserUpload
            },
            content,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal("tenant-a", asset.TenantId);
        Assert.Equal("user-a", asset.OwnerUserId);
        Assert.Equal($"files/{asset.FileId}", asset.ObjectKey);
        Assert.Single(repository.ConversationReferences);
        Assert.Equal(("conversation-a", asset.FileId), repository.ConversationReferences[0]);
        Assert.Equal("# Hello", System.Text.Encoding.UTF8.GetString(objects.LastContent));
    }

    [Fact]
    public async Task ReadTextAsync_NonTextFile_RejectsModelFunctionRead()
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("image.png", "image/png");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadTextAsync(
            asset.FileId,
            new FileAssetScope { TenantId = "tenant-a", UserId = "user-a" },
            CancellationToken.None));
    }

    private static IFileAssetService CreateService(
        RecordingRepository repository,
        RecordingObjectStore objects) => new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MetadataConnectionString = "Data Source=ignored",
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128
            }));

    private static FileAsset CreateAsset(string fileName, string mediaType) => new()
    {
        FileId = "file-a",
        TenantId = "tenant-a",
        OwnerUserId = "user-a",
        FileName = fileName,
        MediaType = mediaType,
        Length = 3,
        Sha256 = "sha",
        ObjectKey = "files/file-a",
        Source = FileAssetSource.UserUpload,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class RecordingRepository : IFileAssetRepository
    {
        public Dictionary<string, FileAsset> Assets { get; } = [];
        public List<(string ConversationId, string FileId)> ConversationReferences { get; } = [];

        public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken)
        {
            Assets.Add(asset.FileId, asset);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken)
        {
            Assets[asset.FileId] = asset;
            return Task.CompletedTask;
        }

        public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult(Assets.GetValueOrDefault(fileId));

        public Task AddConversationReferenceAsync(
            string conversationId,
            string fileId,
            CancellationToken cancellationToken)
        {
            ConversationReferences.Add((conversationId, fileId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingObjectStore : IFileObjectStore
    {
        public byte[] Content { get; set; } = [];
        public byte[] LastContent { get; private set; } = [];

        public async Task<FileObjectReference> WriteAsync(
            FileObjectWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            LastContent = buffer.ToArray();
            Content = LastContent;
            return new FileObjectReference { ObjectKey = $"files/{request.FileId}" };
        }

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult(Content);

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
