using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace OpenAgent.Core.Capabilities.Rag;

internal sealed class RagSearchTool
{
    private readonly IRagService _ragService;
    private readonly ILogger<RagSearchTool> _logger;

    public string Name => "search_knowledge_base";

    public string Description => "Search internal knowledge base for relevant information to answer user questions about policies, procedures, and company information.";

    public string ParametersJsonSchema => """
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
          "required": ["query"]
        }
        """;

    public RagSearchTool(
        IRagService ragService,
        ILogger<RagSearchTool> logger)
    {
        _ragService = ragService;
        _logger = logger;
    }

    internal async Task<string> ExecuteAsync(
        Dictionary<string, object> arguments,
        IAgentUserContext userContext,
        RagConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = arguments.TryGetValue("query", out var queryObj) ? queryObj.ToString() : string.Empty;
            var limit = arguments.TryGetValue("limit", out var limitObj) && int.TryParse(limitObj.ToString(), out var parsedLimit)
                ? parsedLimit : 3;

            if (string.IsNullOrEmpty(query))
            {
                return "Error: Query parameter is required";
            }

            var results = await _ragService.SearchAsync(
                query,
                limit,
                config,
                userContext,
                cancellationToken).ConfigureAwait(false);

            if (!results.Any())
            {
                return "No relevant information found in knowledge base.";
            }

            var resultBuilder = new System.Text.StringBuilder();
            resultBuilder.AppendLine("Search Results from Knowledge Base:");
            resultBuilder.AppendLine("=================================");

            for (int i = 0; i < results.Count; i++)
            {
                resultBuilder.AppendLine($"{i + 1}. {results[i]}");
            }

            return resultBuilder.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagLog.SearchToolFailed(_logger, ex);
            return $"Error searching knowledge base: {ex.Message}";
        }
    }
}
