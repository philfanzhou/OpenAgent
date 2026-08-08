using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Attachments;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentAttachmentReaderTests
{
    [Fact]
    public async Task ReadAsync_ValidImage_ReturnsAttachment()
    {
        AgentAttachmentReader reader = CreateReader();
        FormFile file = CreateFile("image.png", "image/png", [1, 2, 3]);

        IReadOnlyList<AgentAttachment> attachments = await reader.ReadAsync(
            new FormFileCollection { file },
            "tenant-a",
            CancellationToken.None);

        AgentAttachment attachment = Assert.Single(attachments);
        Assert.Equal("image.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Data);
        Assert.NotNull(attachment.Sha256);
    }

    [Theory]
    [InlineData("empty.png", "image/png", 0)]
    [InlineData("script.exe", "application/octet-stream", 1)]
    [InlineData("large.png", "image/png", 5)]
    public async Task ReadAsync_InvalidFile_ThrowsInvalidRequest(
        string fileName,
        string mediaType,
        int length)
    {
        AgentAttachmentReader reader = CreateReader(maxFileSizeBytes: 4);
        FormFile file = CreateFile(fileName, mediaType, new byte[length]);

        await Assert.ThrowsAsync<AgentException>(() => reader.ReadAsync(
            new FormFileCollection { file },
            "tenant-a",
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_TooManyFiles_ThrowsInvalidRequest()
    {
        AgentAttachmentReader reader = CreateReader(maxFileCount: 1);
        var files = new FormFileCollection
        {
            CreateFile("one.txt", "text/plain", [1]),
            CreateFile("two.txt", "text/plain", [2])
        };

        await Assert.ThrowsAsync<AgentException>(() => reader.ReadAsync(
            files,
            "tenant-a",
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MediaTypeDoesNotMatchExtension_ThrowsInvalidRequest()
    {
        AgentAttachmentReader reader = CreateReader();
        FormFile file = CreateFile("payload.png", "text/plain", [1]);

        await Assert.ThrowsAsync<AgentException>(() => reader.ReadAsync(
            new FormFileCollection { file },
            "tenant-a",
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_WithObjectStore_PersistsReference()
    {
        var store = new RecordingAttachmentObjectStore();
        AgentAttachmentReader reader = CreateReader(objectStore: store);

        AgentAttachment attachment = Assert.Single(await reader.ReadAsync(
            new FormFileCollection { CreateFile("notes.txt", "text/plain", [1, 2, 3]) },
            "tenant-a",
            CancellationToken.None));

        Assert.Equal("stored/object.txt", attachment.ObjectKey);
        Assert.Equal("tenant-a", Assert.Single(store.Uploads).TenantId);
        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task ReadAsync_WhenLaterFileIsInvalid_RollsBackStoredObjects()
    {
        var store = new RecordingAttachmentObjectStore();
        AgentAttachmentReader reader = CreateReader(objectStore: store);
        var files = new FormFileCollection
        {
            CreateFile("notes.txt", "text/plain", [1, 2, 3]),
            CreateFile("script.exe", "application/octet-stream", [4])
        };

        await Assert.ThrowsAsync<AgentException>(() => reader.ReadAsync(
            files,
            "tenant-a",
            CancellationToken.None));

        Assert.Equal(["stored/object.txt"], store.DeletedKeys);
    }

    private static AgentAttachmentReader CreateReader(
        int maxFileCount = 5,
        long maxFileSizeBytes = 10,
        IAttachmentObjectStore? objectStore = null)
    {
        var options = new AgentAttachmentOptions
        {
            MaxFileCount = maxFileCount,
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxTotalSizeBytes = 20
        };
        return new AgentAttachmentReader(
            Options.Create(options),
            objectStore ?? new NullAttachmentObjectStore(),
            NullLogger<AgentAttachmentReader>.Instance);
    }

    private static FormFile CreateFile(string fileName, string mediaType, byte[] data)
    {
        var stream = new MemoryStream(data);
        return new FormFile(stream, 0, data.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

    private sealed class RecordingAttachmentObjectStore : IAttachmentObjectStore
    {
        public List<AttachmentObjectUpload> Uploads { get; } = [];
        public List<string> DeletedKeys { get; } = [];

        public async Task<AttachmentObjectReference?> StoreAsync(
            AttachmentObjectUpload upload,
            Stream content,
            CancellationToken cancellationToken)
        {
            Uploads.Add(upload);
            await content.CopyToAsync(Stream.Null, cancellationToken);
            return new AttachmentObjectReference("stored/object.txt", "etag");
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}
