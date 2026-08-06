using System.Text;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Rag;

internal sealed class RagCapabilitySource(IRagService ragService) : ICapabilitySource
{
    private const string Name = "search_knowledge_base";
    private const string Description =
        "Search internal knowledge base for relevant information to answer user questions about policies, procedures, and company information.";
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
          "required": ["query"]
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
                    invocationCancellation))];
        return Task.FromResult(result);
    }

    private async Task<string> SearchAsync(
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
            return "Error: Query parameter is required";
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
            return $"Error searching knowledge base: {exception.Message}";
        }
    }
}
