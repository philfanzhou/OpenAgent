using Microsoft.AspNetCore.Http;
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
            CancellationToken.None);

        AgentAttachment attachment = Assert.Single(attachments);
        Assert.Equal("image.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Data);
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

        await Assert.ThrowsAsync<AgentException>(() => reader.ReadAsync(files, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MediaTypeDoesNotMatchExtension_ThrowsInvalidRequest()
    {
        AgentAttachmentReader reader = CreateReader();
        FormFile file = CreateFile("payload.png", "text/plain", [1]);

        await Assert.ThrowsAsync<AgentException>(() => reader.ReadAsync(
            new FormFileCollection { file },
            CancellationToken.None));
    }

    private static AgentAttachmentReader CreateReader(
        int maxFileCount = 5,
        long maxFileSizeBytes = 10)
    {
        var options = new AgentAttachmentOptions
        {
            MaxFileCount = maxFileCount,
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxTotalSizeBytes = 20
        };
        return new AgentAttachmentReader(Options.Create(options));
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
}
