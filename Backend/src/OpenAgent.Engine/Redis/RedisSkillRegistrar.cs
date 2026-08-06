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
        var skill = new HttpEndpointSkill(item, _httpClientFactory);

        _toolRegistry.RegisterTool(
            new SkillDescriptor
            {
                Id = skill.Name,
                Name = skill.Name,
                Description = skill.Description,
                ParametersJsonSchema = item.ParametersJsonSchema ?? string.Empty,
                Source = SkillSource.Local
            },
            skill.ExecuteAsync);
    }

    private sealed class HttpEndpointSkill
    {
        private readonly SkillInstanceConfig _metadata;
        private readonly IHttpClientFactory _httpClientFactory;

        public HttpEndpointSkill(SkillInstanceConfig metadata, IHttpClientFactory httpClientFactory)
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
                using var client = _httpClientFactory.CreateClient("SkillEndpoint");

                var payload = JsonSerializer.Serialize(arguments);
                var content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await client.PostAsync(
                    _metadata.EndpointUrl,
                    content,
                    cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return responseBody;
                }

                return $"Skill endpoint returned error: {response.StatusCode} - {responseBody}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return $"Skill endpoint call failed: {exception.Message}";
            }
        }
    }
}
