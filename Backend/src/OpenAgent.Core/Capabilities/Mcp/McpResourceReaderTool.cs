using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// MCP resources/read 的平台桥：模型拿工具结果里的 resource URI（或 resource_link）
/// 按需读回资源。文本资源直接回正文；二进制资源经 <see cref="IMcpResourceStore"/>
/// 落盘为文件资产并返回 [File: ...] 描述符，与内嵌 blob 的出口一致。
/// 只暴露当前 agent 已启用且通过鉴权的服务器（可见名即 [MCP:...] 前缀里的名字）；
/// 连接复用 <see cref="McpClientPool"/>。与 search_tools 相同，本工具不单独走
/// AgentAuthorizationGate 的 Tool/Function 门（服务器可见性已是授权结果）。
/// </summary>
internal sealed class McpResourceReaderTool : AIFunction, IToolConcurrencyProvider
{
    internal const string ToolName = "read_mcp_resource";

    private readonly IReadOnlyDictionary<string, McpServerConfig> _servers;
    private readonly McpClientPool _clients;
    private readonly IAgentUserContext _user;
    private readonly IMcpResourceStore _store;

    internal McpResourceReaderTool(
        IReadOnlyDictionary<string, McpServerConfig> servers,
        McpClientPool clients,
        IAgentUserContext user,
        IMcpResourceStore store)
    {
        _servers = servers;
        _clients = clients;
        _user = user;
        _store = store;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Read a resource by URI from an MCP server enabled for this agent; 'server' is the name shown in "
        + "the [MCP:...] prefix of tool descriptions. Text resources return their content inline; binary "
        + "resources are stored as file assets and returned as a [File: ...] fileId descriptor — use "
        + "read_file (text only), publish_files or create_file_transfer_url on that fileId afterwards. "
        + "Pass the URI exactly as the tool returned it (resource links).";

    public override JsonElement JsonSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"server":{"type":"string","description":"MCP server name as shown in [MCP:...] tool prefixes"},"uri":{"type":"string","description":"Resource URI returned by a tool result or resource link"}},"required":["server","uri"],"additionalProperties":false}""")
        .RootElement.Clone();

    public ToolConcurrency Concurrency => ToolConcurrency.ReadOnly;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object?> values = arguments.ToDictionary(
            item => item.Key,
            item => item.Value);
        string? server = values.TryGetValue("server", out object? serverValue)
            ? serverValue?.ToString()
            : null;
        string? uri = values.TryGetValue("uri", out object? uriValue)
            ? uriValue?.ToString()
            : null;
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uri))
        {
            return ToolResult.Error(
                "'server' and 'uri' are required arguments.",
                "invalid_arguments",
                hint: "Use the exact URI a tool returned (resource link).").Content;
        }
        if (!_servers.TryGetValue(server, out McpServerConfig? config))
        {
            return ToolResult.Error(
                $"MCP server '{server}' is not available to this agent.",
                "not_found",
                hint: $"Available servers: {string.Join(", ", _servers.Keys.OrderBy(name => name, StringComparer.Ordinal))}.")
                .Content;
        }

        try
        {
            McpClientPool.AcquiredClient acquired = await _clients.AcquireAsync(
                config, _user, cancellationToken).ConfigureAwait(false);
            ReadResourceResult resource = await acquired.Client.ReadResourceAsync(
                uri,
                options: null,
                cancellationToken).ConfigureAwait(false);
            return await RenderAsync(resource, uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 原始异常可能携带内部细节；净化为可行动信封，模型自行重试或绕开。
            return ToolResult.Error(
                $"Reading resource '{uri}' from MCP server '{server}' failed.",
                "mcp_error",
                hint: "Check the URI against the resource link; the server may also be temporarily unavailable.")
                .Content;
        }
    }

    private async ValueTask<string> RenderAsync(
        ReadResourceResult resource,
        string uri,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (ResourceContents content in resource.Contents)
        {
            switch (content)
            {
                case TextResourceContents text when !string.IsNullOrEmpty(text.Text):
                    lines.Add(text.Text);
                    break;
                case BlobResourceContents blob when blob.DecodedData.Length > 0:
                    FileAsset? asset = await _store.TryStoreAsync(
                        McpResourcePipeline.DeriveFileName(blob.Uri, blob.MimeType),
                        blob.MimeType,
                        blob.DecodedData,
                        cancellationToken).ConfigureAwait(false);
                    lines.Add(asset != null
                        ? McpResourcePipeline.Describe(asset, blob.Uri)
                        : $"{blob.Uri} [binary content: {blob.MimeType ?? "application/octet-stream"}, {blob.DecodedData.Length} bytes]");
                    break;
            }
        }
        if (lines.Count == 0)
        {
            return ToolResult.Error(
                $"The resource '{uri}' returned no readable content.",
                "mcp_error").Content;
        }
        // 单一文本资源直接给正文（与 read_file 的 content 契约对齐）；
        // 多块（或含二进制）逐行给出，模型按行取用。
        return lines.Count == 1 ? lines[0] : string.Join("\n", lines);
    }
}
