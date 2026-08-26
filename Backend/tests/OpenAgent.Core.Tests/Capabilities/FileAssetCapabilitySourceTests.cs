using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class FileAssetCapabilitySourceTests
{
    [Fact]
    public async Task DiscoverAsync_Disabled_ReturnsNoDefinitions()
    {
        FileAssetCapabilitySource source = CreateHarness(
            new FileAssetOptions { Enabled = false }).Source;

        IReadOnlyList<CapabilityDefinition> definitions = await DiscoverAsync(source);

        Assert.Empty(definitions);
    }

    [Fact]
    public async Task DiscoverAsync_MissingExecutionScope_ReturnsNoDefinitions()
    {
        FileAssetCapabilitySource source = CreateHarness(setScope: false).Source;

        IReadOnlyList<CapabilityDefinition> definitions = await DiscoverAsync(source);

        Assert.Empty(definitions);
    }

    [Fact]
    public async Task DiscoverAsync_Enabled_ExposesFileTools()
    {
        FileAssetCapabilitySource source = CreateHarness().Source;

        IReadOnlyList<CapabilityDefinition> definitions = await DiscoverAsync(source);

        string[] names = ["read_file", "write_file", "compress_files", "publish_files"];
        Assert.Equal(names, definitions.Select(definition => definition.Name).ToArray());
    }

    [Fact]
    public async Task InvokeAsync_ReadFileByObjectKey_ReturnsObjectContent()
    {
        TestHarness harness = CreateHarness();
        harness.Objects.ContentsByKey[TenantObjectKey("docs/a.txt")] = "hello"u8.ToArray();
        var arguments = new Dictionary<string, object?>
        {
            ["objectKey"] = TenantObjectKey("docs/a.txt")
        };

        string result = await InvokeAsync(harness.Source, "read_file", arguments);

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("hello", document.RootElement.GetProperty("content").GetString());
        Assert.Equal(TenantObjectKey("docs/a.txt"), document.RootElement.GetProperty("objectKey").GetString());
    }

    [Fact]
    public async Task InvokeAsync_ReadFileByFileId_ReturnsReferencedFileContent()
    {
        TestHarness harness = CreateHarness();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        harness.Repository.Assets[asset.FileId] = asset;
        harness.Repository.References.Add($"conversation-a:{asset.FileId}");
        harness.Objects.ContentsByKey[asset.ObjectKey] = "# Report"u8.ToArray();
        var arguments = new Dictionary<string, object?> { ["fileId"] = asset.FileId };

        string result = await InvokeAsync(harness.Source, "read_file", arguments);

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("# Report", document.RootElement.GetProperty("content").GetString());
        Assert.Equal(asset.FileId, document.RootElement.GetProperty("fileId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_WriteFile_RegistersButDoesNotPublish()
    {
        TestHarness harness = CreateHarness();
        var arguments = new Dictionary<string, object?>
        {
            ["fileName"] = "draft.md",
            ["content"] = "# Draft",
            ["mediaType"] = "text/markdown"
        };

        string result = await InvokeAsync(harness.Source, "write_file", arguments);

        using JsonDocument document = JsonDocument.Parse(result);
        string fileId = document.RootElement.GetProperty("fileId").GetString()!;
        Assert.True(harness.Repository.Assets.ContainsKey(fileId));
        Assert.Contains($"conversation-a:{fileId}", harness.Repository.References);
        Assert.Empty(harness.Context.Published);
        Assert.Equal("draft.md", harness.Objects.LastRequest?.FileName);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task InvokeAsync_ReadFileWithoutExactlyOneIdentifier_ReturnsSanitizedError(
        bool withFileId,
        bool withObjectKey)
    {
        TestHarness harness = CreateHarness();
        var arguments = new Dictionary<string, object?>
        {
            ["fileId"] = withFileId ? "file-a" : null,
            ["objectKey"] = withObjectKey ? TenantObjectKey("a.txt") : null
        };

        string result = await InvokeAsync(harness.Source, "read_file", arguments);

        Assert.StartsWith("文件读取失败：", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ReadFileByForeignTenantObjectKey_ReturnsSanitizedError()
    {
        TestHarness harness = CreateHarness();
        harness.Objects.Content = "# secret"u8.ToArray();
        string foreignKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-b")}" +
            "/users/user-a/notes.md";
        var arguments = new Dictionary<string, object?> { ["objectKey"] = foreignKey };

        string result = await InvokeAsync(harness.Source, "read_file", arguments);

        Assert.StartsWith("文件读取失败：", result, StringComparison.Ordinal);
        Assert.DoesNotContain("# secret", result, StringComparison.Ordinal);
        Assert.Equal(0, harness.Objects.ReadCount);
    }

    [Fact]
    public async Task InvokeAsync_CompressFiles_WritesArchiveAndReturnsResult()
    {
        TestHarness harness = CreateHarness();
        harness.Objects.ContentsByKey[TenantObjectKey("docs/a.txt")] = "alpha"u8.ToArray();
        harness.Objects.ContentsByKey[TenantObjectKey("img/b.csv")] = "1;2;3"u8.ToArray();
        var arguments = new Dictionary<string, object?>
        {
            ["outputName"] = "bundle.zip",
            ["items"] = JsonSerializer.Deserialize<JsonElement>(
                $$"""[{"objectKey":"{{TenantObjectKey("docs/a.txt")}}","fileName":"text/readme.txt"},{"objectKey":"{{TenantObjectKey("img/b.csv")}}"}]""")
        };

        string result = await InvokeAsync(harness.Source, "compress_files", arguments);

        using JsonDocument document = JsonDocument.Parse(result);
        string fileId = document.RootElement.GetProperty("fileId").GetString()!;
        Assert.Equal(2, document.RootElement.GetProperty("fileCount").GetInt32());
        Assert.True(document.RootElement.GetProperty("length").GetInt64() > 0);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("objectKey").GetString()));
        Assert.Equal("bundle.zip", document.RootElement.GetProperty("fileName").GetString());
        Assert.Equal("application/zip", document.RootElement.GetProperty("mediaType").GetString());
        Assert.True(harness.Repository.Assets.ContainsKey(fileId));
        Assert.Contains($"conversation-a:{fileId}", harness.Repository.References);
        Assert.Empty(harness.Context.Published);
        Assert.Equal("bundle.zip", harness.Objects.LastRequest?.FileName);
        Assert.Equal("application/zip", harness.Objects.LastRequest?.MediaType);
        Assert.Equal("tenant-a", harness.Objects.LastRequest?.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_PublishFiles_AssociatesSelectedAssets()
    {
        TestHarness harness = CreateHarness();
        FileAsset markdown = CreateAsset("report.md", "text/markdown");
        FileAsset image = CreateAsset("preview.png", "image/png", "file-b");
        harness.Repository.Assets[markdown.FileId] = markdown;
        harness.Repository.Assets[image.FileId] = image;
        var arguments = new Dictionary<string, object?>
        {
            ["fileIds"] = JsonSerializer.Deserialize<JsonElement>("[\"file-a\",\"file-b\"]")
        };

        string result = await InvokeAsync(harness.Source, "publish_files", arguments);

        using JsonDocument document = JsonDocument.Parse(result);
        JsonElement published = document.RootElement.GetProperty("files");
        Assert.Equal(2, published.GetArrayLength());
        Assert.Equal(["file-a", "file-b"], harness.Context.Published.Select(asset => asset.FileId));
        Assert.Contains("conversation-a:file-a", harness.Repository.References);
        Assert.Contains("conversation-a:file-b", harness.Repository.References);
        Assert.Null(harness.Objects.LastRequest);
    }

    [Fact]
    public async Task InvokeAsync_PublishFilesForUnknownAsset_ReturnsSanitizedError()
    {
        TestHarness harness = CreateHarness();
        var arguments = new Dictionary<string, object?>
        {
            ["fileIds"] = JsonSerializer.Deserialize<JsonElement>("[\"missing-file\"]")
        };

        string result = await InvokeAsync(harness.Source, "publish_files", arguments);

        Assert.StartsWith("文件发布失败：", result, StringComparison.Ordinal);
        Assert.DoesNotContain("missing-file", result, StringComparison.Ordinal);
        Assert.Empty(harness.Context.Published);
    }

    [Fact]
    public async Task InvokeAsync_CompressFilesWithoutOutputName_ReturnsSanitizedError()
    {
        TestHarness harness = CreateHarness();

        string result = await InvokeAsync(
            harness.Source,
            "compress_files",
            new Dictionary<string, object?>());

        Assert.StartsWith("文件压缩失败：", result, StringComparison.Ordinal);
        Assert.Contains("'outputName'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_CompressFilesWithoutItems_ReturnsSanitizedError()
    {
        TestHarness harness = CreateHarness();
        var arguments = new Dictionary<string, object?> { ["outputName"] = "bundle.zip" };

        string result = await InvokeAsync(harness.Source, "compress_files", arguments);

        Assert.StartsWith("文件压缩失败：", result, StringComparison.Ordinal);
        Assert.Contains("'items'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_CompressFilesWithEmptyItems_ReturnsSanitizedError()
    {
        TestHarness harness = CreateHarness();
        var arguments = new Dictionary<string, object?>
        {
            ["outputName"] = "bundle.zip",
            ["items"] = JsonSerializer.Deserialize<JsonElement>("[]")
        };

        string result = await InvokeAsync(harness.Source, "compress_files", arguments);

        Assert.StartsWith("文件压缩失败：", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_CompressFilesWithoutZipExtension_ReturnsSanitizedError()
    {
        TestHarness harness = CreateHarness();
        harness.Objects.ContentsByKey[TenantObjectKey("a.txt")] = "data"u8.ToArray();
        var arguments = new Dictionary<string, object?>
        {
            ["outputName"] = "bundle.tar",
            ["items"] = JsonSerializer.Deserialize<JsonElement>(
                $$"""[{"objectKey":"{{TenantObjectKey("a.txt")}}"}]""")
        };

        string result = await InvokeAsync(harness.Source, "compress_files", arguments);

        Assert.StartsWith("文件压缩失败：", result, StringComparison.Ordinal);
        Assert.Contains(".zip", result, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        ICapabilitySource source) => await source.DiscoverAsync(
            "agent-1",
            new AgentConfig(),
            UserContext(),
            CancellationToken.None);

    private static async Task<string> InvokeAsync(
        FileAssetCapabilitySource source,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        IReadOnlyList<CapabilityDefinition> definitions = await DiscoverAsync(source);
        CapabilityDefinition definition = definitions.Single(item => item.Name == toolName);
        return await definition.Invoke(arguments, CancellationToken.None);
    }

    private static AgentUserContext UserContext() => new()
    {
        UserId = "user-a",
        TenantId = "tenant-a",
        Claims = new Dictionary<string, string>(),
        IsAuthenticated = true
    };

    private static FileAssetOptions DefaultOptions() => new()
    {
        Enabled = true,
        MaxFileSizeBytes = 1024,
        MaxFunctionReadBytes = 128
    };

    private static string TenantObjectKey(string tail) =>
        $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}/users/user-a/{tail}";

    private static FileAsset CreateAsset(
        string fileName,
        string mediaType,
        string fileId = "file-a") => new()
    {
        FileId = fileId,
        TenantId = "tenant-a",
        OwnerUserId = "user-a",
        FileName = fileName,
        MediaType = mediaType,
        Length = 3,
        Sha256 = "sha",
        ObjectKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}" +
            $"/users/user-a/{fileId}",
        Source = FileAssetSource.UserUpload,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static TestHarness CreateHarness(FileAssetOptions? options = null, bool setScope = true)
    {
        FileAssetOptions effective = options ?? DefaultOptions();
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = new FileAssetService(
            repository,
            objects,
            Options.Create(effective));
        var context = new FileAssetExecutionContext();
        if (setScope)
        {
            context.Set(new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            });
        }
        return new TestHarness(repository, objects, context, new FileAssetCapabilitySource(
            service,
            context,
            Options.Create(effective)));
    }

    private sealed record TestHarness(
        RecordingFileAssetRepository Repository,
        RecordingFileObjectStore Objects,
        FileAssetExecutionContext Context,
        FileAssetCapabilitySource Source);
}
