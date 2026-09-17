using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class McpInlineFileTransferTests
{
    private const string Content = "file-body";

    [Fact]
    public async Task ResolveArgumentsAsync_SessionFileId_ReplacedWithEmbeddedResource()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        FileAsset asset = await UploadReferencedFileAsync(files);

        var arguments = new Dictionary<string, object?>
        {
            ["file"] = asset.FileId,
            ["note"] = "keep me"
        };

        await McpInlineFileTransfer.ResolveArgumentsAsync(
            arguments,
            files,
            Scope(),
            legacyBase64: false,
            CancellationToken.None);

        JsonObject payload = Assert.IsType<JsonObject>(arguments["file"]);
        Assert.Equal("resource", payload["type"]!.GetValue<string>());
        JsonObject resource = Assert.IsType<JsonObject>(payload["resource"]);
        Assert.Equal($"openagent://file/{asset.FileId}", resource["uri"]!.GetValue<string>());
        Assert.Equal("application/pdf", resource["mimeType"]!.GetValue<string>());
        byte[] blob = Convert.FromBase64String(resource["blob"]!.GetValue<string>());
        Assert.Equal(Content, System.Text.Encoding.UTF8.GetString(blob));
        Assert.Equal("keep me", arguments["note"]);
    }

    [Fact]
    public async Task ResolveArgumentsAsync_LegacyBase64_ReplacedWithBase64String()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        FileAsset asset = await UploadReferencedFileAsync(files);

        var arguments = new Dictionary<string, object?> { ["file"] = asset.FileId };

        await McpInlineFileTransfer.ResolveArgumentsAsync(
            arguments,
            files,
            Scope(),
            legacyBase64: true,
            CancellationToken.None);

        string payload = Assert.IsType<string>(arguments["file"]);
        Assert.Equal(Content, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    }

    [Fact]
    public async Task ResolveArgumentsAsync_UnknownFileLikeValue_LeavesArgumentUntouched()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        const string unknownId = "0123456789abcdef0123456789abcdef";

        var arguments = new Dictionary<string, object?>
        {
            ["file"] = unknownId,
            ["url"] = "https://files.example/doc.pdf",
            ["page"] = 2
        };

        await McpInlineFileTransfer.ResolveArgumentsAsync(
            arguments,
            files,
            Scope(),
            legacyBase64: false,
            CancellationToken.None);

        Assert.Equal(unknownId, arguments["file"]);
        Assert.Equal("https://files.example/doc.pdf", arguments["url"]);
        Assert.Equal(2, arguments["page"]);
        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task ResolveArgumentsAsync_FileOfOtherUser_LeavesArgumentUntouched()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        FileAsset asset = await UploadReferencedFileAsync(files);

        var arguments = new Dictionary<string, object?> { ["file"] = asset.FileId };

        await McpInlineFileTransfer.ResolveArgumentsAsync(
            arguments,
            files,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-b",
                ConversationId = "conversation-a"
            },
            legacyBase64: false,
            CancellationToken.None);

        Assert.Equal(asset.FileId, arguments["file"]);
        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task CaptureResultFilesAsync_PersistsBinaryBlocksAndAppendsFiles()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        var executionContext = new FileAssetExecutionContext();
        byte[] image = [1, 2, 3];
        byte[] document = [4, 5, 6];
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "converted" },
                new ImageContentBlock
                {
                    // SDK 属性保存的是 base64 文本的 UTF-8 字节（线上格式），
                    // 不是原始二进制；见 BlobResourceContents.Blob 文档。
                    Data = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(image)),
                    MimeType = "image/png"
                },
                new EmbeddedResourceBlock
                {
                    Resource = new BlobResourceContents
                    {
                        Uri = "file:///output/report.pdf",
                        MimeType = "application/pdf",
                        Blob = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(document))
                    }
                }
            ]
        };

        JsonElement captured = await McpInlineFileTransfer.CaptureResultFilesAsync(
            result,
            files,
            Scope(),
            executionContext,
            CancellationToken.None);

        JsonArray content = Assert.IsType<JsonArray>(
            JsonNode.Parse(captured.GetRawText())!["content"]);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("converted", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("file", content[1]!["type"]!.GetValue<string>());
        // image 块没有 URI，按内容位置回退命名。
        Assert.Equal("mcp-output-2.png", content[1]!["fileName"]!.GetValue<string>());
        Assert.Equal("file", content[2]!["type"]!.GetValue<string>());
        Assert.Equal("report.pdf", content[2]!["fileName"]!.GetValue<string>());

        JsonArray filesList = Assert.IsType<JsonArray>(
            JsonNode.Parse(captured.GetRawText())!["files"]);
        Assert.Equal(2, filesList.Count);
        string imageFileId = content[1]!["fileId"]!.GetValue<string>();
        string documentFileId = content[2]!["fileId"]!.GetValue<string>();
        Assert.Equal(imageFileId, filesList[0]!["fileId"]!.GetValue<string>());
        Assert.Equal(documentFileId, filesList[1]!["fileId"]!.GetValue<string>());

        Assert.Equal(2, executionContext.Published.Count);
        Assert.Contains($"conversation-a:{imageFileId}", repository.References);
        Assert.Contains($"conversation-a:{documentFileId}", repository.References);
        Assert.Equal(
            document,
            objects.ContentsByKey.GetValueOrDefault(repository.Assets[documentFileId].ObjectKey));
        Assert.Equal(FileAssetSource.Agent, repository.Assets[imageFileId].Source);
    }

    [Fact]
    public async Task CaptureResultFilesAsync_TextOnlyResult_ReturnsResultWithoutFilesArray()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "done" }]
        };

        JsonElement captured = await McpInlineFileTransfer.CaptureResultFilesAsync(
            result,
            files,
            Scope(),
            new FileAssetExecutionContext(),
            CancellationToken.None);

        Assert.False(captured.TryGetProperty("files", out _));
        Assert.Equal(
            "done",
            captured.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(repository.Assets);
    }

    [Fact]
    public async Task CaptureResultFilesAsync_ResourceLinkBlock_PassesThroughUntouched()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateService(repository, objects);
        var result = new CallToolResult
        {
            Content =
            [
                new ResourceLinkBlock
                {
                    Uri = "https://files.example/report.pdf",
                    Name = "report.pdf",
                    MimeType = "application/pdf"
                }
            ]
        };

        JsonElement captured = await McpInlineFileTransfer.CaptureResultFilesAsync(
            result,
            files,
            Scope(),
            new FileAssetExecutionContext(),
            CancellationToken.None);

        Assert.False(captured.TryGetProperty("files", out _));
        JsonElement block = captured.GetProperty("content")[0];
        Assert.Equal("resource_link", block.GetProperty("type").GetString());
        Assert.Equal("https://files.example/report.pdf", block.GetProperty("uri").GetString());
        Assert.Equal("report.pdf", block.GetProperty("name").GetString());
        Assert.Empty(repository.Assets);
    }

    private static FileAssetScope Scope() => new()
    {
        TenantId = "tenant-a",
        UserId = "user-a",
        ConversationId = "conversation-a"
    };

    private static async Task<FileAsset> UploadReferencedFileAsync(IFileAssetService files)
    {
        await using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Content));
        FileAsset asset = await files.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = "input.pdf",
                MediaType = "application/pdf",
                Source = FileAssetSource.UserUpload
            },
            content,
            Scope(),
            CancellationToken.None);
        await files.EnsureReferencesAsync([asset.FileId], Scope(), CancellationToken.None);
        return asset;
    }

    private static IFileAssetService CreateService(
        RecordingFileAssetRepository repository,
        RecordingFileObjectStore objects) => new FileAssetService(
        repository,
        objects,
        Options.Create(new FileAssetOptions
        {
            Enabled = true,
            MaxFileSizeBytes = 1024,
            MaxFunctionReadBytes = 128
        }));
}
