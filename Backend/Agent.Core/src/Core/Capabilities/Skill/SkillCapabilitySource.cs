using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillCapabilitySource(ISkillProvider skills) : ICapabilitySource
{
    public async Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SkillDescriptor> descriptors = await skills.GetSkillDescriptorsAsync(
            agentId,
            user,
            config.Skills,
            cancellationToken).ConfigureAwait(false);
        return descriptors.Select(descriptor => new CapabilityDefinition(
            descriptor.Name,
            descriptor.Description,
            descriptor.ParametersJsonSchema,
            AgentResourceType.Skill,
            descriptor.Name,
            (arguments, invocationCancellation) => skills.ExecuteAsync(
                descriptor.Name,
                ToValues(arguments),
                user,
                invocationCancellation))).ToList().AsReadOnly();
    }

    private static Dictionary<string, object> ToValues(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.ToDictionary(item => item.Key, item => item.Value ?? string.Empty);
}
