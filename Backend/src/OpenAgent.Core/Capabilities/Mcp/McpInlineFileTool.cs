using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// Wraps MCP tools listed in <see cref="Contracts.Configuration.McpServerConfig.InlineFileTools"/>:
/// string arguments matching a session-referenced OpenAgent fileId are replaced with the MCP
/// embedded-resource payload before invocation, and binary blocks in the result are persisted
/// as downloadable FileAssets. The tool schema passes through unchanged.
/// </summary>
internal sealed class McpInlineFileTool : AIFunction
{
    private const string DescriptionSuffix =
        " File arguments accept OpenAgent fileIds from the current conversation; "
        + "inline payloads are supplied automatically. Binary result blocks are "
        + "registered as downloadable files.";

    private readonly McpClientTool _inner;
    private readonly bool _legacyBase64;
    private readonly IFileAssetService _files;
    private readonly FileAssetExecutionContext _executionContext;

    private McpInlineFileTool(
        McpClientTool inner,
        bool legacyBase64,
        IFileAssetService files,
        FileAssetExecutionContext executionContext)
    {
        _inner = inner;
        _legacyBase64 = legacyBase64;
        _files = files;
        _executionContext = executionContext;
    }

    internal static AITool Create(
        McpClientTool inner,
        bool legacyBase64,
        IFileAssetService files,
        FileAssetExecutionContext executionContext) =>
        new McpInlineFileTool(inner, legacyBase64, files, executionContext);

    public override string Name => _inner.Name;

    public override string Description => _inner.Description + DescriptionSuffix;

    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> callArguments = [];
        foreach (KeyValuePair<string, object?> argument in arguments)
        {
            callArguments[argument.Key] = argument.Value;
        }

        FileAssetScope scope = _executionContext.Scope
            ?? throw new InvalidOperationException(
                "MCP inline file transfer requires an active file execution scope.");
        await McpInlineFileTransfer.ResolveArgumentsAsync(
            callArguments,
            _files,
            scope,
            _legacyBase64,
            cancellationToken).ConfigureAwait(false);

        CallToolResult result = await _inner.CallAsync(
            callArguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await McpInlineFileTransfer.CaptureResultFilesAsync(
            result,
            _files,
            scope,
            _executionContext,
            cancellationToken).ConfigureAwait(false);
    }
}
