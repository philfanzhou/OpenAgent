using System.Net;
using System.Text;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
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
        SkillCapabilitySource source = Source(registry);

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
        SkillCapabilitySource source = Source(registry);

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
        SkillCapabilitySource source = Source(registry);

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

    [Fact]
    public async Task DiscoverAsync_ObjectStoredSkill_ReadsPackageAndInvokesEndpoint()
    {
        byte[] package = Encoding.UTF8.GetBytes(
            "{\"id\":\"weather\",\"name\":\"weather\",\"description\":\"Weather lookup\",\"endpointUrl\":\"https://skills.example.test/weather\"}");
        var objectStore = new FakeObjectStore(package);
        var registry = new SkillRegistry();
        SkillCapabilitySource source = Source(registry, objectStore);
        var config = new AgentConfig
        {
            Skills = new SkillsConfig
            {
                EnabledSkills = ["weather"],
                Instances =
                [
                    new SkillInstanceConfig
                    {
                        Id = "weather",
                        Name = "weather",
                        Enabled = true,
                        PackageFileName = "skill.json",
                        ObjectKey = "skills/weather.json"
                    }
                ]
            }
        };

        IReadOnlyList<CapabilityDefinition> capabilities = await source.DiscoverAsync(
            "current-agent",
            config,
            User("user"),
            default);
        CapabilityDefinition capability = Assert.Single(capabilities);
        string result = await capability.Invoke(
            new Dictionary<string, object?> { ["city"] = "Singapore" },
            default);

        Assert.Equal(1, objectStore.ReadCount);
        Assert.Equal("weather", capability.ResourceId);
        Assert.Contains("Singapore", result, StringComparison.Ordinal);
    }

    private static SkillCapabilitySource Source(
        SkillRegistry registry,
        FakeObjectStore? objectStore = null) => new(
            registry,
            new ObjectStoredSkillProvider(
                objectStore ?? new FakeObjectStore([]),
                new SkillPackageReader(),
                new FakeHttpClientFactory()));

    private static SkillDescriptor Descriptor(string name) => new()
    {
        Id = name,
        Name = name,
        Description = $"{name} skill",
        ParametersJsonSchema = "{\"type\":\"object\"}"
    };

    private static AgentUserContext User(string userId) => new() { UserId = userId };

    private sealed class FakeObjectStore(byte[] content) : IFileObjectStore
    {
        public int ReadCount { get; private set; }

        public Task<FileObjectReference> WriteAsync(
            FileObjectWriteRequest request,
            Stream contentStream,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(content);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new EchoHandler());
    }

    private sealed class EchoHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string payload = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            };
        }
    }
}
