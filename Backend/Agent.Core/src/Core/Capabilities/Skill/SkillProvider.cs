using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Capabilities.Skill;

internal class SkillProvider : ISkillProvider
{
    private readonly IAgentConfigProvider _configProvider;
    private readonly SkillCatalog _catalog;

    public SkillProvider(
        IAgentConfigProvider configProvider,
        SkillCatalog catalog)
    {
        _configProvider = configProvider;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<SkillDescriptor>> GetSkillDescriptorsAsync(
        string? agentId, IAgentUserContext? userContext, SkillsConfig? overrideConfig = null, CancellationToken cancellationToken = default)
    {
        var skillsConfig = overrideConfig;

        if (skillsConfig == null)
        {
            var config = !string.IsNullOrEmpty(agentId)
                ? await _configProvider.GetConfigAsync(agentId, cancellationToken)
                : await _configProvider.GetConfigAsync(cancellationToken);
            skillsConfig = config?.Skills;
        }

        List<SkillDescriptor> allDescriptors = _catalog.GetTools().ToList();

        var result = FilterByConfig(allDescriptors, skillsConfig);
        result = FilterByPermission(result, userContext);

        return result.AsReadOnly();
    }

    public async Task<string> ExecuteAsync(
        string skillName, Dictionary<string, object> arguments, IAgentUserContext? userContext, CancellationToken cancellationToken = default)
    {
        return await _catalog.ExecuteToolAsync(
            skillName,
            arguments,
            cancellationToken).ConfigureAwait(false);
    }

    public void RegisterSkill(ISkill skill, SkillSource source = SkillSource.Local, string? sourceId = null)
    {
        _catalog.RegisterSkill(skill, source, sourceId);
    }

    private static List<SkillDescriptor> FilterByConfig(List<SkillDescriptor> descriptors, SkillsConfig? config)
    {
        if (config == null) return descriptors;

        if (config.Instances.Count > 0)
        {
            var enabledInstances = config.Instances.Where(i => i.Enabled).ToList();
            if (enabledInstances.Count > 0)
            {
                return descriptors
                    .Select(descriptor => (
                        Descriptor: descriptor,
                        Instance: enabledInstances.FirstOrDefault(instance =>
                            Matches(descriptor, instance))))
                    .Where(item => item.Instance != null)
                    .Select(item => ApplyInstance(item.Descriptor, item.Instance!))
                    .ToList();
            }
        }

        if (config.EnabledSkills.Count > 0)
        {
            var enabled = config.EnabledSkills.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return descriptors.Where(d => enabled.Contains(d.Name)).ToList();
        }

        // 如果明确配置了 EnabledSkills 为空数组，则不启用任何 skill
        return new List<SkillDescriptor>();
    }

    private static bool Matches(
        SkillDescriptor descriptor,
        SkillInstanceConfig instance) =>
        string.Equals(instance.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(instance.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(instance.Id, descriptor.Name, StringComparison.OrdinalIgnoreCase);

    private static SkillDescriptor ApplyInstance(
        SkillDescriptor descriptor,
        SkillInstanceConfig instance) => new()
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

    private static List<SkillDescriptor> FilterByPermission(List<SkillDescriptor> descriptors, IAgentUserContext? userContext)
    {
        return descriptors.Where(d => IsAllowedForUser(d, userContext)).ToList();
    }

    internal static bool IsAllowedForUser(SkillDescriptor descriptor, IAgentUserContext? userContext)
    {
        if (descriptor.AllowedUserIds.Count == 0
            && descriptor.AllowedGroups.Count == 0
            && descriptor.AllowedTenantIds.Count == 0
            && descriptor.AllowedRoles.Count == 0)
        {
            return true;
        }

        if (userContext == null) return false;

        if (descriptor.AllowedUserIds.Count > 0 && descriptor.AllowedUserIds.Contains(userContext.UserId))
            return true;

        if (descriptor.AllowedGroups.Count > 0 && userContext.Groups != null
            && descriptor.AllowedGroups.Intersect(userContext.Groups).Any())
            return true;

        if (descriptor.AllowedTenantIds.Count > 0 && userContext.TenantId != null
            && descriptor.AllowedTenantIds.Contains(userContext.TenantId))
            return true;

        if (descriptor.AllowedRoles.Count > 0 && userContext.Roles != null
            && descriptor.AllowedRoles.Intersect(userContext.Roles).Any())
            return true;

        return false;
    }
}
