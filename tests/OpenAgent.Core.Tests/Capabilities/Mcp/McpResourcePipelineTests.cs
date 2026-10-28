using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Files.Requests;
using OpenAgent.Core.Mapping;
using OpenAgent.Core.Tooling.Invocation;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities.Mcp;

public class McpResourcePipelineTests
{
    private static readonly FileAssetScope Scope = new()
    {
        TenantId = "tenant-1",
        UserId = "user-1",
        ConversationId = "conversation-1"
    };

    private static readonly FileAsset SampleAsset = new()
    {
        FileId = "fa-mcp-1",
        TenantId = "tenant-1",
        OwnerUserId = "user-1",
        FileName = "report.pdf",
        MediaType = "application/pdf",
        Length = 3,
        Sha256 = "00",
        ObjectKey = "objects/fa-mcp-1",
        Source = FileAssetSource.Agent,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static DataContent BlobDataContent(
        string uri,
        byte[] bytes,
        string mimeType = "application/pdf") =>
        new(new ReadOnlyMemory<byte>(bytes), mimeType)
        {
            RawRepresentation = new EmbeddedResourceBlock
            {
                Resource = BlobResourceContents.FromBytes(bytes, uri, mimeType)
            }
        };

    /// <summary>可控的文件服务替身：记录上传文件名，Result=null 模拟存储拒绝，
    /// InfrastructureFailure 模拟 S3 网络等基础设施故障。</summary>
    private sealed class RecordingFileService : IFileAssetService
    {
        public List<string> FileNames { get; } = [];

        public FileAsset? Result { get; set; } = SampleAsset;

        public Exception? InfrastructureFailure { get; set; }

        public Task<FileAsset> UploadAsync(
            FileAssetCreateRequest request,
            Stream content,
            FileAssetScope scope,
            CancellationToken cancellationToken)
        {
            FileNames.Add(request.FileName);
            if (InfrastructureFailure is { } failure)
            {
                throw failure;
            }
            return Result is { } asset
                ? Task.FromResult(asset)
                : throw new AgentException(AgentErrorCode.InvalidRequest, "storage declined");
        }

        public Task EnsureReferencesAsync(
            IReadOnlyList<string> fileIds,
            FileAssetScope scope,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FileAsset?> GetAsync(string fileId, FileAssetScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FileAsset?> GetReferencedAsync(string fileId, FileAssetScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FileAsset>> ListAsync(FileAssetScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FileAssetContent> ReadAsync(string fileId, FileAssetScope scope, CancellationToken cancellationToken, long? maxBytes = null) =>
            throw new NotSupportedException();

        public Task<string> ReadTextAsync(string fileId, FileAssetScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> ReadObjectAsync(string objectKey, FileAssetScope scope, CancellationToken cancellationToken, long? maxBytes = null) =>
            throw new NotSupportedException();

        public Task<string> ReadObjectTextAsync(string objectKey, FileAssetScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FileArchiveResult> CompressAsync(FileArchiveRequest request, FileAssetScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Rewrite_ArrayWithBlobResource_PersistsAndReplacesWithDescriptor()
    {
        var files = new RecordingFileService();
        AIContent[] contents =
        [
            new TextContent("caption"),
            BlobDataContent("mem://files/report.pdf", [1, 2, 3])
        ];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, files, Scope, logger: null, CancellationToken.None);

        var rewrittenContents = Assert.IsAssignableFrom<IEnumerable<AIContent>>(rewritten);
        Assert.Equal(
            "caption\n[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf",
            ToolResultText.JoinContents(rewrittenContents));
        Assert.Equal(["report.pdf"], files.FileNames);
    }

    [Fact]
    public async Task Rewrite_SingleBlobDataContent_PersistsAndReplaces()
    {
        var files = new RecordingFileService();

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            BlobDataContent("mem://files/report.pdf", [1, 2, 3]),
            files,
            Scope,
            logger: null,
            CancellationToken.None);

        Assert.Equal(
            "[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf",
            ToolResultText.Render(rewritten));
    }

    [Fact]
    public async Task Rewrite_CallToolResultJson_BlobReplacedRendersText()
    {
        var files = new RecordingFileService();
        JsonElement json = JsonSerializer.SerializeToElement(new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "done" },
                new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(
                        new byte[] { 1, 2, 3 }, "mem://files/report.pdf", "application/pdf")
                },
                new ResourceLinkBlock { Uri = "mem://spec", Name = "spec" }
            ],
            IsError = true
        }, McpJsonUtilities.DefaultOptions);

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            json, files, Scope, logger: null, CancellationToken.None);

        // JsonElement 形态在管道内归一成文本：落盘的 blob 换描述符、resource_link
        // 保留 URI、isError 加前缀，JsonElement 不再外泄。
        Assert.Equal(
            "[tool error] done\n"
            + "[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf\n"
            + "mem://spec",
            Assert.IsType<string>(rewritten));
    }

    [Fact]
    public async Task Rewrite_CallToolResultJson_TextOnly_RendersTextWithoutStore()
    {
        var files = new RecordingFileService();
        JsonElement json = JsonSerializer.SerializeToElement(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "report body" }],
            StructuredContent = JsonDocument.Parse("{\"rows\":2}").RootElement.Clone()
        }, McpJsonUtilities.DefaultOptions);

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            json, files, Scope, logger: null, CancellationToken.None);

        Assert.Equal("report body\n{\"rows\":2}", Assert.IsType<string>(rewritten));
        Assert.Empty(files.FileNames);
    }

    [Fact]
    public async Task Rewrite_NonCallToolResultJson_ReturnedUnchanged()
    {
        var files = new RecordingFileService();
        using JsonDocument document = JsonDocument.Parse("{\"items\":[1,2]}");
        JsonElement json = document.RootElement.Clone();

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            json, files, Scope, logger: null, CancellationToken.None);

        Assert.Equal(json.GetRawText(), Assert.IsType<JsonElement>(rewritten).GetRawText());
        Assert.Empty(files.FileNames);
    }

    [Fact]
    public async Task Rewrite_StoreDeclines_FallsBackToBinaryPlaceholder()
    {
        var files = new RecordingFileService { Result = null };
        AIContent[] contents = [BlobDataContent("mem://files/report.pdf", [1, 2, 3])];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, files, Scope, logger: null, CancellationToken.None);

        Assert.Equal(
            "mem://files/report.pdf [binary content: application/pdf, 3 bytes]",
            ToolResultText.Render(rewritten));
    }

    [Fact]
    public async Task Rewrite_NoScope_SkipsPersistence()
    {
        var files = new RecordingFileService();
        AIContent[] contents = [BlobDataContent("mem://files/report.pdf", [1, 2, 3])];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, files, scope: null, logger: null, CancellationToken.None);

        Assert.Same(contents, rewritten);
        Assert.Empty(files.FileNames);
    }

    [Fact]
    public async Task Rewrite_StorageInfrastructureFailure_FallsBackWithoutThrowing()
    {
        // S3 网络故障等基础设施异常不在旧的窄捕获面内：曾会击穿到隔离层，
        // 把本已成功的 MCP 调用整体变成 tool error。必须回退占位符。
        var files = new RecordingFileService
        {
            InfrastructureFailure = new HttpRequestException("S3 unreachable")
        };
        AIContent[] contents = [BlobDataContent("mem://files/report.pdf", [1, 2, 3])];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, files, Scope, logger: null, CancellationToken.None);

        Assert.Equal(
            "mem://files/report.pdf [binary content: application/pdf, 3 bytes]",
            ToolResultText.Render(rewritten));
    }

    [Fact]
    public async Task Rewrite_MoreBlobsThanCap_OnlyPersistsUpToCap()
    {
        var files = new RecordingFileService();
        AIContent[] contents = Enumerable.Range(0, 10)
            .Select(index => BlobDataContent($"mem://files/report-{index}.pdf", [1, 2, 3]))
            .ToArray();

        await McpResourcePipeline.RewriteAsync(contents, files, Scope, logger: null, CancellationToken.None);

        Assert.Equal(McpResourcePipeline.MaxPersistedBlobsPerResult, files.FileNames.Count);
    }

    [Fact]
    public async Task Rewrite_PlainBinaryContent_NotPersisted()
    {
        var files = new RecordingFileService();
        AIContent[] contents =
        [
            new TextContent("caption"),
            new DataContent(new ReadOnlyMemory<byte>([1, 2, 3]), "image/png")
        ];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, files, Scope, logger: null, CancellationToken.None);

        Assert.Equal(
            "caption\n[binary content: image/png, 3 bytes]",
            ToolResultText.Render(rewritten));
        Assert.Empty(files.FileNames);
    }

    [Fact]
    public async Task Rewrite_TextOnlyResult_ReturnedUnchanged()
    {
        var files = new RecordingFileService();
        AIContent[] contents = [new TextContent("plain")];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, files, Scope, logger: null, CancellationToken.None);

        Assert.Same(contents, rewritten);
        Assert.Empty(files.FileNames);
    }

    [Theory]
    [InlineData("https://srv/dl/report%20final.pdf?token=1", null, "report final.pdf")]
    [InlineData("mem://res/manifest", "application/pdf", "manifest.pdf")]
    // 扩展名经 FileMediaTypeCatalog 反查：目录覆盖 .xlsx 等全部白名单类型。
    [InlineData("mem://res/sheet", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "sheet.xlsx")]
    // 无 URI 段也无可反查 MIME：不强行补后缀，由存储层白名单裁决（回退占位符）。
    [InlineData("mem://res/", null, "mcp-resource")]
    [InlineData("mem://res/notes.md", null, "notes.md")]
    public void DeriveFileName_FromUriAndMimeType_ProducesStorableName(
        string uri,
        string? mimeType,
        string expected)
    {
        Assert.Equal(expected, McpResourcePipeline.DeriveFileName(uri, mimeType));
    }

    [Fact]
    public async Task Wrap_McpBlobTool_RendersDescriptorThroughIsolatedPipeline()
    {
        var files = new RecordingFileService();
        var filesContext = new FileAssetExecutionContext();
        filesContext.Set(new TurnContext
        {
            TenantId = "tenant-1",
            UserId = "user-1",
            ConversationId = "conversation-1",
            TraceId = "trace-1"
        });
        AITool tool = new StubTool("mcp__srv__report", _ => ValueTask.FromResult<object?>(
            new AIContent[]
            {
                new TextContent("caption"),
                BlobDataContent("mem://files/report.pdf", [1, 2, 3])
            }));
        AITool wrapped = IsolatedToolFunction.Wrap(
            McpResourcePipeline.Wrap(tool, files, filesContext, logger: null),
            budget: ToolResultBudget.Unlimited);

        object? result = await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Equal(
            "caption\n[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf",
            result);
    }

    private sealed class StubTool(string name, Func<AIFunctionArguments, ValueTask<object?>> invoke) : AIFunction
    {
        public override string Name => name;

        public override string Description => "stub";

        public override JsonElement JsonSchema =>
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => invoke(arguments);
    }
}
