using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class SkillCapabilitySourceTests
{
    [Fact]
    public async Task DiscoverAsync_EnabledSkill_IsExposedAndInvokesRegistry()
    {
        var registry = new SkillRegistry();
        registry.RegisterTool(
            Descriptor("weather"),
            (arguments, _) => Task.FromResult($"weather:{arguments["city"]}"));
        var source = new SkillCapabilitySource(registry);

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "agent",
            new AgentConfig { Skills = new SkillsConfig { EnabledSkills = ["weather"] } },
            User("user"),
            default);
        CapabilityDefinition capability = Assert.Single(capabilities);
        string result = await capability.Invoke(
            new Dictionary<string, object?> { ["city"] = "Shanghai" },
            default);

        Assert.Equal("weather", capability.Name);
        Assert.Equal("weather:Shanghai", result);
    }

    [Fact]
    public async Task DiscoverAsync_DisabledInstances_FallsBackToEnabledSkills()
    {
        var registry = new SkillRegistry();
        registry.RegisterTool(Descriptor("weather"), (_, _) => Task.FromResult("ok"));
        var source = new SkillCapabilitySource(registry);

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "agent",
            new AgentConfig
            {
                Skills = new SkillsConfig
                {
                    EnabledSkills = ["weather"],
                    Instances = [new SkillInstanceConfig { Name = "weather", Enabled = false }]
                }
            },
            User("user"),
            default);

        Assert.Single(capabilities);
    }

    [Fact]
    public async Task DiscoverAsync_InstanceAcl_ExcludesUnavailableSkill()
    {
        var registry = new SkillRegistry();
        registry.RegisterTool(Descriptor("weather"), (_, _) => Task.FromResult("ok"));
        var source = new SkillCapabilitySource(registry);

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "agent",
            new AgentConfig
            {
                Skills = new SkillsConfig
                {
                    Instances =
                    [
                        new SkillInstanceConfig
                        {
                            Name = "weather",
                            AllowedUserIds = ["other"]
                        }
                    ]
                }
            },
            User("user"),
            default);

        Assert.Empty(capabilities);
    }

    private static SkillDescriptor Descriptor(string name) => new()
    {
        Id = name,
        Name = name,
        Description = $"{name} skill",
        ParametersJsonSchema = "{\"type\":\"object\"}"
    };

    private static AgentUserContext User(string userId) => new() { UserId = userId };
}
