using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillCapabilitySource(
    SkillRegistry registry,
    ObjectStoredSkillProvider objectStoredSkills) : ICapabilitySource
{
    public async Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SkillDescriptor> descriptors = GetAvailableDescriptors(
            registry.GetTools(),
            config.Skills,
            user);
        var result = descriptors.Select(descriptor => new CapabilityDefinition(
            descriptor.Name,
            descriptor.Description,
            descriptor.ParametersJsonSchema,
            AgentResourceType.Skill,
            descriptor.Name,
            (arguments, invocationCancellation) => registry.ExecuteToolAsync(
                descriptor.Name,
                ToValues(arguments),
                invocationCancellation))).ToList();

        foreach (SkillInstanceConfig instance in config.Skills.Instances.Where(IsObjectStoredAndEnabled))
        {
            StoredSkillDefinition stored = await objectStoredSkills
                .LoadAsync(instance, cancellationToken)
                .ConfigureAwait(false);
            if (!IsAllowedForUser(stored.Descriptor, user))
            {
                continue;
            }

            result.Add(new CapabilityDefinition(
                stored.Descriptor.Name,
                stored.Descriptor.Description,
                stored.Descriptor.ParametersJsonSchema,
                AgentResourceType.Skill,
                stored.Descriptor.Id,
                stored.ExecuteAsync));
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<SkillDescriptor> GetAvailableDescriptors(
        IReadOnlyList<SkillDescriptor> descriptors,
        SkillsConfig config,
        IAgentUserContext user)
    {
        IEnumerable<SkillDescriptor> configured;
        List<SkillInstanceConfig> enabledInstances = config.Instances
            .Where(instance => instance.Enabled && string.IsNullOrWhiteSpace(instance.ObjectKey))
            .ToList();
        if (enabledInstances.Count > 0)
        {
            configured = ApplyInstances(descriptors, enabledInstances);
        }
        else if (config.EnabledSkills.Count > 0)
        {
            configured = descriptors.Where(descriptor => config.EnabledSkills.Contains(
                descriptor.Name,
                StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            configured = [];
        }
        return configured
            .Where(descriptor => IsAllowedForUser(descriptor, user))
            .ToList()
            .AsReadOnly();
    }

    private static IEnumerable<SkillDescriptor> ApplyInstances(
        IReadOnlyList<SkillDescriptor> descriptors,
        IReadOnlyList<SkillInstanceConfig> instances)
    {
        foreach (SkillInstanceConfig instance in instances)
        {
            SkillDescriptor? descriptor = descriptors.FirstOrDefault(candidate => Matches(candidate, instance));
            if (descriptor != null)
            {
                yield return new SkillDescriptor
                {
                    Id = descriptor.Id,
                    Name = descriptor.Name,
                    Description = string.IsNullOrWhiteSpace(instance.Description)
                        ? descriptor.Description
                        : instance.Description,
                    ParametersJsonSchema = string.IsNullOrWhiteSpace(instance.ParametersJsonSchema)
                        ? descriptor.ParametersJsonSchema
                        : instance.ParametersJsonSchema,
                    Source = descriptor.Source,
                    SourceId = descriptor.SourceId,
                    AllowedUserIds = instance.AllowedUserIds,
                    AllowedGroups = instance.AllowedGroups,
                    AllowedTenantIds = instance.AllowedTenantIds,
                    AllowedRoles = instance.AllowedRoles
                };
            }
        }
    }

    private static bool Matches(SkillDescriptor descriptor, SkillInstanceConfig instance) =>
        string.Equals(instance.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(instance.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(instance.Id, descriptor.Name, StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectStoredAndEnabled(SkillInstanceConfig instance) =>
        instance.Enabled && !string.IsNullOrWhiteSpace(instance.ObjectKey);

    private static bool IsAllowedForUser(SkillDescriptor descriptor, IAgentUserContext user)
    {
        if (descriptor.AllowedUserIds.Count == 0
            && descriptor.AllowedGroups.Count == 0
            && descriptor.AllowedTenantIds.Count == 0
            && descriptor.AllowedRoles.Count == 0)
        {
            return true;
        }

        return (descriptor.AllowedUserIds.Count > 0
                && descriptor.AllowedUserIds.Contains(user.UserId))
            || (descriptor.AllowedGroups.Count > 0
                && user.Groups != null
                && descriptor.AllowedGroups.Intersect(user.Groups).Any())
            || (descriptor.AllowedTenantIds.Count > 0
                && user.TenantId != null
                && descriptor.AllowedTenantIds.Contains(user.TenantId))
            || (descriptor.AllowedRoles.Count > 0
                && user.Roles != null
                && descriptor.AllowedRoles.Intersect(user.Roles).Any());
    }

    private static Dictionary<string, object> ToValues(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.ToDictionary(item => item.Key, item => item.Value ?? string.Empty);
}
