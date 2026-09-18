using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class AgentExecutorSkillToolTests
{
    private const string SkillName = "report-writer";
    private const string ResourcePath = "references/summary.md";

    [Fact]
    public async Task ExecuteStreamingAsync_SkillResourceTool_InvokedWithServices()
    {
        // MAF's read_skill_resource tool declares a required IServiceProvider parameter.
        // Without function invocation services on FunctionInvokingChatClient, argument
        // binding fails with "Services are required for parameter 'serviceProvider'"
        // and the conversation ends in a 500.
        var provider = new SequenceChatProvider(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "read_skill_resource",
                        new Dictionary<string, object?>
                        {
                            ["skillName"] = SkillName,
                            ["resourceName"] = ResourcePath
                        })])
            ],
            [new ChatResponseUpdate(ChatRole.Assistant, "resource loaded")]
        ]);
        var objects = new RecordingFileObjectStore();
        var catalog = new SkillCatalog();
        RegisterSkillPackage(objects, catalog);

        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            provider,
            configure: services =>
            {
                services.RemoveAll<IFileObjectStore>();
                services.AddSingleton<IFileObjectStore>(objects);
                services.RemoveAll<ISkillCatalog>();
                services.AddSingleton<ISkillCatalog>(catalog);
                services.RemoveAll<IAgentRuntimeResolver>();
                services.AddSingleton<IAgentRuntimeResolver>(new SkillRuntimeResolver());
            });

        await foreach (AgentStreamEvent _ in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("skill-resource-conversation"),
            User,
            CancellationToken.None))
        {
        }

        Assert.True(provider.Requests.Count >= 2, "Expected a followup request carrying the tool result.");
        FunctionResultContent result = Assert.Single(
            provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal("call-1", result.CallId);
        Assert.Null(result.Exception);
        Assert.Contains("Q3 revenue", result.Result?.ToString(), StringComparison.Ordinal);
    }

    private static void RegisterSkillPackage(RecordingFileObjectStore objects, SkillCatalog catalog)
    {
        var instance = new SkillInstanceConfig
        {
            TenantId = User.TenantId!,
            Id = "skill-1",
            Name = SkillName,
            Type = SkillTypes.AgentSkill,
            Enabled = true,
            ObjectKey = "files/tenants/" + FileObjectTenantScope.CreatePartition(User.TenantId!)
                + "/skill-packages/skill-1",
            PackageFormat = "directory",
            ScriptExecutionEnabled = false
        };
        byte[] skillMd = Encoding.UTF8.GetBytes($"""
            ---
            name: {SkillName}
            description: Writes reports from templates.
            ---

            # Report writer

            Read {ResourcePath} for the numbers.
            """);
        byte[] resource = Encoding.UTF8.GetBytes("Q3 revenue: 42\n");
        string baseKey = instance.ObjectKey!;
        var index = new SkillPackageStorageIndex
        {
            TenantId = User.TenantId!,
            Files =
            [
                new SkillPackageStorageFile
                {
                    RelativePath = "SKILL.md",
                    ObjectKey = baseKey + "/0",
                    Sha256 = Convert.ToHexString(SHA256.HashData(skillMd)).ToLowerInvariant()
                },
                new SkillPackageStorageFile
                {
                    RelativePath = ResourcePath,
                    ObjectKey = baseKey + "/1",
                    Sha256 = Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant()
                }
            ]
        };
        objects.ContentsByKey[baseKey] = JsonSerializer.SerializeToUtf8Bytes(index);
        objects.ContentsByKey[baseKey + "/0"] = skillMd;
        objects.ContentsByKey[baseKey + "/1"] = resource;
        catalog.Register(instance);
    }

    private sealed class SkillRuntimeResolver : IAgentRuntimeResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            string llmProfileId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRuntimeProfile
            {
                AgentId = agentId,
                Config = new AgentConfig
                {
                    MaxTurns = 3,
                    Skills = new SkillsConfig
                    {
                        Instances = [new SkillInstanceConfig
                        {
                            TenantId = userContext.TenantId!,
                            Id = "skill-1",
                            Name = SkillName,
                            Type = SkillTypes.AgentSkill,
                            Enabled = true,
                            ObjectKey = "files/tenants/"
                                + FileObjectTenantScope.CreatePartition(userContext.TenantId!)
                                + "/skill-packages/skill-1",
                            PackageFormat = "directory",
                            ScriptExecutionEnabled = false
                        }]
                    }
                },
                Model = new LlmConfig { ModelId = "configured-model" }
            });
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "read the skill resource",
        AgentId = "test-agent",
        LlmProfileId = "test-model",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };
}
