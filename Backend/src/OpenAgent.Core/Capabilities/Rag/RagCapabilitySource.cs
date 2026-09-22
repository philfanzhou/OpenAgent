using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Rag;

internal sealed class RagCapabilitySource(
    IRagService ragService,
    ILogger<RagCapabilitySource> logger) : ICapabilitySource
{
    private const string Name = "search_knowledge_base";
    private const string Description =
        "Search the internal knowledge base for policies, procedures and company information. "
        + "Use it before answering questions about internal topics; do not use it for general knowledge "
        + "or questions the conversation already answers. "
        + "Results are numbered excerpts; cite them in the answer instead of paraphrasing blindly.";
    private const string ParametersJsonSchema = """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "The search query to find relevant documents in the knowledge base"
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of results to return (default: 3)",
              "minimum": 1,
              "maximum": 10
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> result = !config.Rag.Enabled
            ? []
            : [new CapabilityDefinition(
                Name,
                Description,
                ParametersJsonSchema,
                AgentResourceType.Tool,
                Name,
                (arguments, invocationCancellation) => SearchAsync(
                    arguments,
                    user,
                    config.Rag,
                    invocationCancellation),
                Concurrency: ToolConcurrency.ReadOnly)];
        return Task.FromResult(result);
    }

    private async Task<ToolResult> SearchAsync(
        IReadOnlyDictionary<string, object?> arguments,
        IAgentUserContext user,
        RagConfig config,
        CancellationToken cancellationToken)
    {
        string query = arguments.TryGetValue("query", out object? queryValue)
            ? queryValue?.ToString() ?? string.Empty
            : string.Empty;
        int limit = arguments.TryGetValue("limit", out object? limitValue)
            && int.TryParse(limitValue?.ToString(), out int parsedLimit)
                ? parsedLimit
                : 3;

        if (string.IsNullOrEmpty(query))
        {
            return ToolResult.Error(
                "The 'query' parameter is required.",
                "invalid_arguments",
                hint: "Provide a non-empty search query.");
        }

        try
        {
            List<string> results = await ragService.SearchAsync(
                query,
                limit,
                config,
                user,
                cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
            {
                return "No relevant information found in knowledge base.";
            }

            var resultBuilder = new StringBuilder();
            resultBuilder.AppendLine("Search Results from Knowledge Base:");
            resultBuilder.AppendLine("=================================");
            for (int index = 0; index < results.Count; index++)
            {
                resultBuilder.AppendLine($"{index + 1}. {results[index]}");
            }

            return resultBuilder.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 原始异常只进日志；模型拿到的是可行动的净化错误，而不是底层堆栈细节。
            logger.LogError(exception, "Knowledge base search failed for query {Query}", query);
            return ToolResult.Error(
                "Knowledge base search failed.",
                "search_failed",
                hint: "Retry with a narrower query; if it keeps failing, answer without the knowledge base and say so.");
        }
    }
}
