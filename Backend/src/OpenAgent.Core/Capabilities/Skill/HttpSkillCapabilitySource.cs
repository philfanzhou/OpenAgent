using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class HttpSkillCapabilitySource(
    ISkillCatalog catalog,
    IHttpClientFactory httpClientFactory) : ICapabilitySource
{
    public async Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.TenantId))
        {
            return [];
        }

        var skills = new List<SkillInstanceConfig>();
        foreach (string skillId in config.Skills.EnabledSkills)
        {
            SkillInstanceConfig? skill = await catalog.GetAsync(
                user.TenantId,
                skillId,
                SkillTypes.HttpEndpoint,
                cancellationToken).ConfigureAwait(false);
            if (skill != null)
            {
                skills.Add(skill);
            }
        }

        skills.AddRange(config.Skills.Instances.Where(skill =>
            skill.Enabled
            && string.Equals(skill.TenantId, user.TenantId, StringComparison.Ordinal)
            && string.Equals(skill.Type, SkillTypes.HttpEndpoint, StringComparison.OrdinalIgnoreCase)
            && !skills.Any(existing => string.Equals(existing.Id, skill.Id, StringComparison.OrdinalIgnoreCase))));

        return skills
            .Where(skill => skill.Enabled && Uri.TryCreate(skill.EndpointUrl, UriKind.Absolute, out Uri? endpoint)
                && endpoint.Scheme is "http" or "https")
            .Select(skill => new CapabilityDefinition(
                skill.Name,
                skill.Description,
                skill.ParametersJsonSchema,
                AgentResourceType.Skill,
                skill.Id,
                (arguments, invocationCancellation) => ExecuteAsync(
                    skill.EndpointUrl!,
                    arguments,
                    invocationCancellation)))
            .ToList()
            .AsReadOnly();
    }

    private async Task<string> ExecuteAsync(
        string endpointUrl,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("SkillEndpoint");
        string payload = JsonSerializer.Serialize(arguments);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(
            endpointUrl,
            content,
            cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? responseBody
            : $"Skill endpoint returned error: {response.StatusCode} - {responseBody}";
    }
}
