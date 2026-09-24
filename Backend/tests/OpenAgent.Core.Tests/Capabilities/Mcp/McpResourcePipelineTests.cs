using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities.Mcp;

public class McpResourcePipelineTests
{
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

    private sealed class RecordingStore : IMcpResourceStore
    {
        public List<string> FileNames { get; } = [];

        public FileAsset? Result { get; set; } = SampleAsset;

        public ValueTask<FileAsset?> TryStoreAsync(
            string fileName,
            string? mediaType,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            FileNames.Add(fileName);
            return ValueTask.FromResult(Result);
        }
    }

    [Fact]
    public async Task Rewrite_ArrayWithBlobResource_PersistsAndReplacesWithDescriptor()
    {
        var store = new RecordingStore();
        AIContent[] contents =
        [
            new TextContent("caption"),
            BlobDataContent("mem://files/report.pdf", [1, 2, 3])
        ];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, store, logger: null, CancellationToken.None);

        var rewrittenContents = Assert.IsAssignableFrom<IEnumerable<AIContent>>(rewritten);
        Assert.Equal(
            "caption\n[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf",
            ToolResultText.JoinContents(rewrittenContents));
        Assert.Equal(["report.pdf"], store.FileNames);
    }

    [Fact]
    public async Task Rewrite_SingleBlobDataContent_PersistsAndReplaces()
    {
        var store = new RecordingStore();

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            BlobDataContent("mem://files/report.pdf", [1, 2, 3]),
            store,
            logger: null,
            CancellationToken.None);

        Assert.Equal(
            "[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf",
            ToolResultText.Render(rewritten));
    }

    [Fact]
    public async Task Rewrite_CallToolResultJson_BlobReplacedDescriptorRestPreserved()
    {
        var store = new RecordingStore();
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
            json, store, logger: null, CancellationToken.None);

        Assert.Equal(
            "[tool error] done\n"
            + "[File: report.pdf] fileId=fa-mcp-1 (application/pdf, 3 bytes) from mem://files/report.pdf\n"
            + "mem://spec",
            ToolResultText.Render(rewritten));
    }

    [Fact]
    public async Task Rewrite_StoreDeclines_FallsBackToBinaryPlaceholder()
    {
        var store = new RecordingStore { Result = null };
        AIContent[] contents = [BlobDataContent("mem://files/report.pdf", [1, 2, 3])];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, store, logger: null, CancellationToken.None);

        Assert.Equal(
            "mem://files/report.pdf [binary content: application/pdf, 3 bytes]",
            ToolResultText.Render(rewritten));
    }

    [Fact]
    public async Task Rewrite_MoreBlobsThanCap_OnlyPersistsUpToCap()
    {
        var store = new RecordingStore();
        AIContent[] contents = Enumerable.Range(0, 10)
            .Select(index => BlobDataContent($"mem://files/report-{index}.pdf", [1, 2, 3]))
            .ToArray();

        await McpResourcePipeline.RewriteAsync(contents, store, logger: null, CancellationToken.None);

        Assert.Equal(McpResourcePipeline.MaxPersistedBlobsPerResult, store.FileNames.Count);
    }

    [Fact]
    public async Task Rewrite_PlainBinaryContent_NotPersisted()
    {
        var store = new RecordingStore();
        AIContent[] contents =
        [
            new TextContent("caption"),
            new DataContent(new ReadOnlyMemory<byte>([1, 2, 3]), "image/png")
        ];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, store, logger: null, CancellationToken.None);

        Assert.Equal(
            "caption\n[binary content: image/png, 3 bytes]",
            ToolResultText.Render(rewritten));
        Assert.Empty(store.FileNames);
    }

    [Fact]
    public async Task Rewrite_TextOnlyResult_ReturnedUnchanged()
    {
        var store = new RecordingStore();
        AIContent[] contents = [new TextContent("plain")];

        object? rewritten = await McpResourcePipeline.RewriteAsync(
            contents, store, logger: null, CancellationToken.None);

        Assert.Same(contents, rewritten);
        Assert.Empty(store.FileNames);
    }

    [Theory]
    [InlineData("https://srv/dl/report%20final.pdf?token=1", null, "report final.pdf")]
    [InlineData("mem://res/manifest", "application/pdf", "manifest.pdf")]
    [InlineData("mem://res/", null, "mcp-resource.bin")]
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
        var store = new RecordingStore();
        AITool tool = new StubTool("mcp__srv__report", _ => ValueTask.FromResult<object?>(
            new AIContent[]
            {
                new TextContent("caption"),
                BlobDataContent("mem://files/report.pdf", [1, 2, 3])
            }));
        AITool wrapped = IsolatedToolFunction.Wrap(
            McpResourcePersistingFunction.Wrap(tool, store, logger: null),
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
