using System.Net.Http.Json;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Core.Capabilities.Rag;

internal class QdrantAdapter : IRagAdapter
{
    public string AdapterName => RagAdapterType.Qdrant;

    public bool CanHandle(RagInstanceConfig config)
    {
        return string.Equals(config.Type, RagAdapterType.Qdrant, StringComparison.OrdinalIgnoreCase)
            || (config.ApiEndpoint?.Contains("qdrant", StringComparison.OrdinalIgnoreCase) == true);
    }

    public HttpRequestMessage BuildSearchRequest(RagInstanceConfig config, string query, int limit, Dictionary<string, object>? filters)
    {
        // Qdrant Search API
        // 参考：https://qdrant.tech/documentation/api/#search-points
        var request = new
        {
            // 使用查询向量或使用全文搜索（如果配置了）
            // 这里使用简化的配置，假设使用全文搜索或传入预计算的向量
            limit = limit,
            with_payload = true,
            with_vector = false,
            filter = BuildQdrantFilter(filters)
        };

        // 如果配置了查询文本字段，使用文本搜索；否则使用预计算向量
        var queryField = GetQueryField(config, "content");
        var apiUrl = $"{config.ApiEndpoint}/collections/{config.CollectionName}/points/search";

        // 构建请求
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(request)
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            httpRequest.Headers.Add("api-key", config.ApiKey);
        }

        return httpRequest;
    }

    public List<SearchResult> ParseSearchResponse(RagInstanceConfig config, HttpResponseMessage response)
    {
        var result = response.Content.ReadFromJsonAsync<QdrantSearchResponse>().GetAwaiter().GetResult();
        return result?.Result?.Select(r => new SearchResult
        {
            Content = ExtractContent(r.Payload, config),
            Metadata = r.Payload ?? new Dictionary<string, object>(),
            RelevanceScore = r.Score ?? 0.0,
            SourceId = r.Id?.ToString() ?? string.Empty,
            RagInstanceId = config.Id
        }).ToList() ?? new List<SearchResult>();
    }

    public HttpRequestMessage? BuildIndexRequest(RagInstanceConfig config, string content, Dictionary<string, object>? metadata)
    {
        // Qdrant 点插入 API
        // 注意：Qdrant 需要向量，这里简化实现，实际使用时需要调用嵌入模型
        var request = new
        {
            points = new[]
            {
                new
                {
                    payload = new Dictionary<string, object>(metadata ?? new())
                    {
                        ["content"] = content
                    },
                    // 向量字段需要实际嵌入，这里暂时用空向量
                    vector = new float[0]
                }
            }
        };

        var apiUrl = $"{config.ApiEndpoint}/collections/{config.CollectionName}/points";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(request)
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            httpRequest.Headers.Add("api-key", config.ApiKey);
        }

        return httpRequest;
    }

    private static object? BuildQdrantFilter(Dictionary<string, object>? filters)
    {
        if (filters == null || filters.Count == 0)
        {
            return null;
        }

        var mustConditions = new List<object>();
        foreach (var filter in filters)
        {
            mustConditions.Add(new
            {
                key = filter.Key,
                match = new { value = filter.Value }
            });
        }

        return new { must = mustConditions };
    }

    private static string GetQueryField(RagInstanceConfig config, string defaultValue)
    {
        if (config.AdapterConfig?.TryGetValue("query_field", out var field) == true && !string.IsNullOrEmpty(field))
        {
            return field;
        }
        return defaultValue;
    }

    private static string ExtractContent(Dictionary<string, object>? payload, RagInstanceConfig config)
    {
        if (payload == null)
        {
            return string.Empty;
        }

        var contentField = GetQueryField(config, "content");
        if (payload.TryGetValue(contentField, out var contentValue))
        {
            return contentValue?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private class QdrantSearchResponse
    {
        public List<QdrantPoint>? Result { get; set; }
    }

    private class QdrantPoint
    {
        public object? Id { get; set; }
        public double? Score { get; set; }
        public Dictionary<string, object>? Payload { get; set; }
    }
}
