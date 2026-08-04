using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Core.Capabilities.Rag;

internal class RagFlowAdapter : IRagAdapter
{
    public string AdapterName => RagAdapterType.RagFlow;

    public bool CanHandle(RagInstanceConfig config)
    {
        return string.Equals(config.Type, RagAdapterType.RagFlow, StringComparison.OrdinalIgnoreCase)
            || (config.ApiEndpoint?.Contains("ragflow", StringComparison.OrdinalIgnoreCase) == true);
    }

    public HttpRequestMessage BuildSearchRequest(RagInstanceConfig config, string query, int limit, Dictionary<string, object>? filters)
    {
        var request = new
        {
            dataset_ids = new string[] { GetKnowledgeBaseId(config) },
            question = query,
            top_k = limit
        };

        var endpoint = GetSearchEndpoint(config);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request)
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        }

        return httpRequest;
    }

    public List<SearchResult> ParseSearchResponse(RagInstanceConfig config, HttpResponseMessage response)
    {
        var result = response.Content.ReadFromJsonAsync<RetrievalResponse>().GetAwaiter().GetResult();
        var res = result?.Data?.Chunks?.Select(r => new SearchResult
        {
            Content = r.Content ?? string.Empty,
            SourceId = r.Id ?? string.Empty,
            RagInstanceId = config.Id,
            RelevanceScore = r.Similarity
        }).ToList() ?? new List<SearchResult>();
        return res;
    }

    public HttpRequestMessage? BuildIndexRequest(RagInstanceConfig config, string content, Dictionary<string, object>? metadata)
    {
        return null;
    }

    private static string GetSearchEndpoint(RagInstanceConfig config)
    {
        if (config.AdapterConfig?.TryGetValue("search_endpoint", out var endpoint) == true && !string.IsNullOrEmpty(endpoint))
        {
            return $"{config.ApiEndpoint.TrimEnd('/')}/{endpoint.TrimStart('/')}";
        }
        return $"{config.ApiEndpoint.TrimEnd('/')}/api/v1/retrieval";
    }

    private static string GetKnowledgeBaseId(RagInstanceConfig config)
    {
        if (config.AdapterConfig?.TryGetValue("knowledge_base_id", out var kbId) == true && !string.IsNullOrEmpty(kbId))
        {
            return kbId;
        }
        if (config.AdapterConfig?.TryGetValue("knowledge_id", out kbId) == true && !string.IsNullOrEmpty(kbId))
        {
            return kbId;
        }
        return config.CollectionName;
    }

    public sealed class RetrievalResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("data")]
        public RetrievalData? Data { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public sealed class RetrievalData
    {
        [JsonPropertyName("chunks")]
        public List<RetrievalChunk>? Chunks { get; set; }
    }

    public sealed class RetrievalChunk
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("document_name")]
        public string? DocumentName { get; set; }

        [JsonPropertyName("doc_name")]
        public string? DocName { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("similarity")]
        public double Similarity { get; set; }
    }
}
