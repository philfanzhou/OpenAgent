using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal class RedisSkillRegistrar : RedisRegistrarBase<SkillInstanceConfig>
{
    private static readonly JsonSerializerOptions SkillJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IToolRegistry _toolRegistry;
    private readonly IHttpClientFactory _httpClientFactory;

    public RedisSkillRegistrar(
        IRedisConnectionProvider redis,
        IToolRegistry toolRegistry,
        ILogger<RedisSkillRegistrar> logger,
        IHttpClientFactory httpClientFactory)
        : base(redis, logger)
    {
        _toolRegistry = toolRegistry;
        _httpClientFactory = httpClientFactory;
    }

    protected override string RegistrarName => "Skill";
    protected override string IndexKey => "skill:published:index";
    protected override string ItemKeyPrefix => "skill:registry";

    protected override SkillInstanceConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<SkillInstanceConfig>(json, SkillJsonOptions);

    protected override string? GetItemId(SkillInstanceConfig item) => item.Name;

    protected override void Register(SkillInstanceConfig item)
    {
        var mockSkill = new RedisMockSkill(item, _httpClientFactory);

        _toolRegistry.RegisterTool(
            new SkillDescriptor
            {
                Id = mockSkill.Name,
                Name = mockSkill.Name,
                Description = mockSkill.Description,
                ParametersJsonSchema = item.ParametersJsonSchema ?? string.Empty,
                Source = SkillSource.Local
            },
            mockSkill.ExecuteAsync);
    }

    private sealed class RedisMockSkill
    {
        private readonly SkillInstanceConfig _metadata;
        private readonly IHttpClientFactory _httpClientFactory;

        public RedisMockSkill(SkillInstanceConfig metadata, IHttpClientFactory httpClientFactory)
        {
            _metadata = metadata;
            _httpClientFactory = httpClientFactory;
            Name = metadata.Name;
            Description = metadata.Description;
        }

        public string Name { get; }
        public string Description { get; }

        public async Task<string> ExecuteAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            if (string.Equals(_metadata.Type, "HttpEndpoint", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(_metadata.EndpointUrl))
            {
                return await ExecuteHttpEndpointAsync(arguments, cancellationToken);
            }

            return $"Skill '{_metadata.Name}' is not configured with a valid endpoint. Type: {_metadata.Type ?? "null"}, EndpointUrl: {_metadata.EndpointUrl ?? "null"}";
        }

        private async Task<string> ExecuteHttpEndpointAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            try
            {
                // Factory-created client: inherits Core's skip-cert handler and respects DNS refresh.
                // 30s timeout preserved from the previous static HttpClient contract.
                using var client = _httpClientFactory.CreateClient("SkillEndpoint");
                client.Timeout = TimeSpan.FromSeconds(30);

                var payload = JsonSerializer.Serialize(arguments);
                var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(_metadata.EndpointUrl, content, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return responseBody;
                }

                return $"Skill endpoint returned error: {response.StatusCode} - {responseBody}";
            }
            catch (Exception ex)
            {
                return $"Skill endpoint call failed: {ex.Message}";
            }
        }
    }
}
