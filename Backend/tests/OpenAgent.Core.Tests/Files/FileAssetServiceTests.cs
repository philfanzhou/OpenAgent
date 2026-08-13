using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public class FileAssetServiceTests
{
    [Fact]
    public async Task UploadAsync_StoresAssetBeforeItIsReferencedByAConversationMessage()
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
        Assert.Equal($"files/tenant-a/user-a/{asset.FileId}", asset.ObjectKey);
        Assert.Equal("tenant-a", objects.LastRequest?.TenantId);
        Assert.Equal("user-a", objects.LastRequest?.UserId);
        Assert.Equal("# Hello", System.Text.Encoding.UTF8.GetString(objects.LastContent));
        Assert.Empty(repository.References);
    }

    [Fact]
    public async Task ReadAsync_NoConversationReference_RejectsWithoutReadingObject()
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Theory]
    [InlineData("tenant-b", "user-a")]
    [InlineData("tenant-a", "user-b")]
    public async Task ReadAsync_NotOwner_RejectsEvenWithReference(string tenantId, string userId)
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = tenantId,
                UserId = userId,
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task ReadTextAsync_NonTextFile_RejectsModelFunctionRead()
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("image.png", "image/png");
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadTextAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task EnsureReferencesAsync_OnlyAssociatesOwnedFiles()
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore();
        FileAsset mine = CreateAsset("notes.md", "text/markdown");
        FileAsset other = new()
        {
            FileId = "file-other",
            TenantId = "tenant-b",
            OwnerUserId = "user-a",
            FileName = "other.md",
            MediaType = "text/markdown",
            Length = 3,
            Sha256 = "sha",
            ObjectKey = "files/other",
            Source = FileAssetSource.UserUpload,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
        repository.Assets[mine.FileId] = mine;
        repository.Assets[other.FileId] = other;
        IFileAssetService service = CreateService(repository, objects);

        await service.EnsureReferencesAsync(
            [mine.FileId, other.FileId],
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None);

        Assert.Contains($"conversation-a:{mine.FileId}", repository.References);
        Assert.DoesNotContain($"conversation-a:{other.FileId}", repository.References);
    }

    [Fact]
    public async Task EnsureReferencesAsync_IsIdempotent()
    {
        var repository = new RecordingRepository();
        var objects = new RecordingObjectStore();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);
        FileAssetScope scope = new()
        {
            TenantId = "tenant-a",
            UserId = "user-a",
            ConversationId = "conversation-a"
        };

        await service.EnsureReferencesAsync([asset.FileId], scope, CancellationToken.None);
        await service.EnsureReferencesAsync([asset.FileId], scope, CancellationToken.None);

        Assert.Single(repository.References);
        Assert.Contains($"conversation-a:{asset.FileId}", repository.References);
    }

    private static IFileAssetService CreateService(
        RecordingRepository repository,
        RecordingObjectStore objects) => new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
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
        ObjectKey = "files/tenant-a/user-a/file-a",
        Source = FileAssetSource.UserUpload,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class RecordingRepository : IFileAssetRepository
    {
        public Dictionary<string, FileAsset> Assets { get; } = [];
        public HashSet<string> References { get; } = new(StringComparer.Ordinal);

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

        public Task EnsureConversationReferencesAsync(
            string conversationId,
            IReadOnlyList<string> fileIds,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
        {
            foreach (string fileId in fileIds)
            {
                References.Add($"{conversationId}:{fileId}");
            }
            return Task.CompletedTask;
        }

        public Task<bool> IsReferencedAsync(
            string conversationId,
            string fileId,
            CancellationToken cancellationToken) =>
            Task.FromResult(References.Contains($"{conversationId}:{fileId}"));
    }

    private sealed class RecordingObjectStore : IFileObjectStore
    {
        public byte[] Content { get; set; } = [];
        public byte[] LastContent { get; private set; } = [];
        public FileObjectWriteRequest? LastRequest { get; private set; }
        public int ReadCount { get; private set; }

        public async Task<FileObjectReference> WriteAsync(
            FileObjectWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            LastContent = buffer.ToArray();
            Content = LastContent;
            return new FileObjectReference
            {
                ObjectKey = $"files/{request.TenantId}/{request.UserId}/{request.FileId}"
            };
        }

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Content);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
