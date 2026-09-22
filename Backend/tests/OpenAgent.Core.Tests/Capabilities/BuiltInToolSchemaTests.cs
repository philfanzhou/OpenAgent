using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Capabilities.UserProfile;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

/// <summary>
/// 内置能力 schema 的 lint：所有工具声明必须是合法 JSON、封闭
/// （additionalProperties:false）、required 引用真实存在的属性。
/// 该测试替代旧版 NormalizeSchema 的静默降级，在 CI 里把带病 schema 拦下来。
/// </summary>
public class BuiltInToolSchemaTests
{
    public static TheoryData<string> BuiltInTools()
    {
        TheoryData<string> data = [];
        foreach (CapabilityDefinitionHarness harness in CreateHarnesses())
        {
            foreach (string name in harness.Names)
            {
                data.Add(name);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInTools))]
    public async Task BuiltInSchemas_AreClosedAndConsistent(string toolName)
    {
        foreach (CapabilityDefinitionHarness harness in CreateHarnesses())
        {
            IReadOnlyList<CapabilityDefinition> definitions = await harness.Source.DiscoverAsync(
                "agent-1", harness.Config, User(), CancellationToken.None);
            CapabilityDefinition? definition = definitions.SingleOrDefault(item => item.Name == toolName);
            if (definition == null)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(definition.ParametersJsonSchema);
            ValidateSchemaNode(toolName, document.RootElement);
        }
    }

    [Fact]
    public async Task CreateAsync_InvalidSchemaInDevelopment_ThrowsInsteadOfDegrading()
    {
        var source = new FakeSource(new CapabilityDefinition(
            "broken_tool",
            "schema is not JSON",
            "{\"type\":\"object\",",
            AgentResourceType.Tool,
            "test/broken_tool",
            (_, _) => Task.FromResult<OpenAgent.Contracts.Capabilities.ToolResult>("ok")));
        var factory = new CapabilityToolFactory(
            [source],
            new AgentAuthorizationGate(new AllowAllAgentAuthorizationService()),
            NullLogger<CapabilityToolFactory>.Instance,
            new TestHostEnvironment { EnvironmentName = Environments.Development });

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(
            "agent-1", new AgentConfig(), User(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_InvalidSchemaInProduction_DegradesToParameterless()
    {
        var source = new FakeSource(new CapabilityDefinition(
            "broken_tool",
            "schema is not JSON",
            "{\"type\":\"object\",",
            AgentResourceType.Tool,
            "test/broken_tool",
            (_, _) => Task.FromResult<OpenAgent.Contracts.Capabilities.ToolResult>("ok")));
        var factory = new CapabilityToolFactory(
            [source],
            new AgentAuthorizationGate(new AllowAllAgentAuthorizationService()),
            NullLogger<CapabilityToolFactory>.Instance,
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        IReadOnlyList<Microsoft.Extensions.AI.AITool> tools = await factory.CreateAsync(
            "agent-1", new AgentConfig(), User(), CancellationToken.None);

        Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(Assert.Single(tools));
    }

    private static void ValidateSchemaNode(string toolName, JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("properties", out JsonElement properties))
        {
            return;
        }
        Assert.True(
            node.TryGetProperty("additionalProperties", out JsonElement additional)
                && additional.ValueKind == JsonValueKind.False,
            $"[{toolName}] schema with properties must set additionalProperties:false");
        Assert.Equal(JsonValueKind.Object, properties.ValueKind);

        HashSet<string> declared = properties.EnumerateObject().Select(property => property.Name).ToHashSet();
        if (node.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement requiredName in required.EnumerateArray())
            {
                Assert.Contains(requiredName.GetString(), declared);
            }
        }
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            Assert.True(
                property.Value.TryGetProperty("type", out _),
                $"[{toolName}] property '{property.Name}' must declare a type");
        }
        if (node.TryGetProperty("items", out JsonElement items))
        {
            ValidateSchemaNode(toolName, items);
        }
    }

    private static IEnumerable<CapabilityDefinitionHarness> CreateHarnesses()
    {
        var fileOptions = Options.Create(new FileAssetOptions { Enabled = true });
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = new FileAssetService(repository, objects, fileOptions);
        var context = new FileAssetExecutionContext();
        context.Set(TurnContexts.Create("tenant-a", "user-a", "conversation-a"));
        yield return new CapabilityDefinitionHarness(
            new FileAssetCapabilitySource(
                files,
                new FileShareService(
                    files,
                    new RecordingFileShareRepository(),
                    objects,
                    Options.Create(new FileShareOptions())),
                context,
                fileOptions,
                new FileAssetUrlDownloader(Mock.Of<IHttpClientFactory>(), fileOptions)),
            new AgentConfig(),
            ["read_file", "create_file_transfer_url", "list_files", "write_file", "compress_files", "publish_files", "download_file"]);

        var codeContext = new FileAssetExecutionContext();
        codeContext.Set(TurnContexts.Create("tenant-a", "user-a", "conversation-a"));
        yield return new CapabilityDefinitionHarness(
            new CodeCapabilitySource(
                null!,
                null!,
                codeContext,
                new AgentAuthorizationGate(new AllowAllAgentAuthorizationService()),
                Options.Create(new CodeExecutionOptions { Enabled = true })),
            new AgentConfig { CodeExecution = new CodeExecutionConfig { Enabled = true } },
            ["execute_code"]);

        yield return new CapabilityDefinitionHarness(
            new RagCapabilitySource(null!, NullLogger<RagCapabilitySource>.Instance),
            new AgentConfig { Rag = new RagConfig { Enabled = true } },
            ["search_knowledge_base"]);

        yield return new CapabilityDefinitionHarness(
            new UserProfileCapabilitySource(),
            new AgentConfig(),
            ["get_current_user_profile"]);
    }

    private static AgentUserContext User() => new()
    {
        UserId = "user-a",
        TenantId = "tenant-a",
        Claims = new Dictionary<string, string>(),
        IsAuthenticated = true
    };

    private sealed record CapabilityDefinitionHarness(
        ICapabilitySource Source,
        AgentConfig Config,
        string[] Names);

    private sealed class FakeSource(CapabilityDefinition definition) : ICapabilitySource
    {
        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityDefinition>>([definition]);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider? ContentRootFileProvider { get; set; }
    }
}
