using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Models;

public interface IRagAdapter
{
    string AdapterName { get; }

    bool CanHandle(RagInstanceConfig config);

    HttpRequestMessage BuildSearchRequest(RagInstanceConfig config, string query, int limit, Dictionary<string, object>? filters);

    List<SearchResult> ParseSearchResponse(RagInstanceConfig config, HttpResponseMessage response);

    HttpRequestMessage? BuildIndexRequest(RagInstanceConfig config, string content, Dictionary<string, object>? metadata);
}
