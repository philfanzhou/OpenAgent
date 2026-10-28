using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files.Requests;
using OpenAgent.Core.Tooling.Abstractions;
using OpenAgent.Core.Tooling.Invocation;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpFeatureFactory(
    McpToolFactory tools,
    IFileAssetService fileAssets,
    FileAssetExecutionContext files,
    IOptions<McpExecutionOptions> options,
    ILogger<IsolatedToolFunction> toolLogger,
    ILogger<McpFeatureFactory> logger) : IAgentFeatureFactory
{
    public async Task<AgentFeature> CreateAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        McpToolRuntime runtime = await tools.CreateAsync(
            agentId, config.Mcp, user, cancellationToken).ConfigureAwait(false);
        int threshold = options.Value.DeferredToolThreshold;
        if (threshold > 0 && runtime.Tools.Count > threshold)
        {
            McpFeatureLog.ToolsDeferred(logger, runtime.Tools.Count, threshold);
        }
        return new AgentFeature
        {
            Tools = runtime.Tools,
            InlineTools = runtime.ResourceReader is { } reader ? [reader] : [],
            DeferredToolThreshold = threshold,
            PrepareTool = Prepare,
            Resource = runtime
        };
    }

    private AITool Prepare(AITool tool) => tool.Name.StartsWith("mcp__", StringComparison.Ordinal)
        ? McpResourcePipeline.Wrap(tool, fileAssets, files, toolLogger)
        : tool;
}
