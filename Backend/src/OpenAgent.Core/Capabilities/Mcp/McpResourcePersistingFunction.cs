using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// MCP 工具的落盘装饰器：调用原工具后，把结果里的内嵌二进制资源经
/// <see cref="McpResourcePipeline"/> 重写为文件描述符。位于
/// <see cref="OpenAgent.Core.Runtime.Agent.IsolatedToolFunction"/> 内层，落盘耗时计入
/// 单次调用超时、失败不外抛（存储层已降级为 null 回退）。
/// </summary>
internal sealed class McpResourcePersistingFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IMcpResourceStore _store;
    private readonly ILogger? _logger;

    private McpResourcePersistingFunction(AIFunction inner, IMcpResourceStore store, ILogger? logger)
    {
        _inner = inner;
        _store = store;
        _logger = logger;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        object? result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        return await McpResourcePipeline.RewriteAsync(result, _store, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>非 AIFunction 的工具原样返回（与 IsolatedToolFunction.Wrap 的口径一致）。</summary>
    internal static AITool Wrap(AITool tool, IMcpResourceStore store, ILogger? logger = null) =>
        tool is AIFunction function ? new McpResourcePersistingFunction(function, store, logger) : tool;
}
