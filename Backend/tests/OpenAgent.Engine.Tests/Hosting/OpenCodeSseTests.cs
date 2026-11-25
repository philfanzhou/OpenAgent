using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAgent.Application.Execution;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Engine.Hosting;
using OpenAgent.Runtime;
using OpenAgent.Runtime.Tests;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class OpenCodeSseTests
{
    [OpenCodeAndPiFact]
    public async Task HttpSse_CanSwitchAnExistingOpenCodeConversationToPi()
    {
        const string conversationId = "switch-opencode-to-pi";
        int requests = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            int index = Interlocked.Increment(ref requests);
            string payload = request.GetRawText();
            if (index == 1)
            {
                string tool = request.GetProperty("tools").EnumerateArray()
                    .Select(item => item.GetProperty("function").GetProperty("name").GetString()!)
                    .Single(name => name.EndsWith("get_current_user_profile", StringComparison.Ordinal));
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant",
                    tool_calls = new[] { new { index = 0, id = "fixture-profile", type = "function", function = new { name = tool, arguments = "{}" } } }
                }, "tool_calls");
            }
            else if (index == 2)
            {
                Assert.Contains("tenant-1", payload);
                await ModelFixture.TextAsync(context, "Committed OpenCode reply");
            }
            else
            {
                Assert.Equal(3, index);
                Assert.Contains("Initial OpenCode question", payload);
                Assert.Contains("Committed OpenCode reply", payload);
                Assert.Contains("Follow up using Pi", payload);
                await ModelFixture.TextAsync(context, "Pi continued the conversation");
            }
        });
        string root = Path.Combine(Path.GetTempPath(), "acp-switch-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenCode:Executable"] = Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE"),
                ["OpenCode:WorkspaceRoot"] = root,
                ["OpenCode:TurnTimeoutSeconds"] = "60",
                ["Pi:Enabled"] = "true",
                ["Pi:Executable"] = Environment.GetEnvironmentVariable("OPENAGENT_TEST_PI_ACP"),
                ["Pi:AllowedTenants:0"] = "tenant-1"
            }).Build();
            await using StreamingHost host = await StreamingHost.StartAsync(
                configure: services =>
                {
                    services.AddAgentHost(configuration, options =>
                    {
                        options.EnableJwtAuth = false;
                        options.EnableSwagger = false;
                    });
                    services.AddOpenCodeRuntime(configuration);
                },
                model: new LlmConfig { ModelId = "fixture", Endpoint = model.Endpoint, ApiKey = "fixture-key", ContextTokens = 100_000 },
                configureApplication: app => app.MapRuntimeToolBridge(),
                useConversationRuns: true);
            Assert.True(await host.Store.CreateAsync(new ConversationRecord
            {
                ConversationId = conversationId, TenantId = "tenant-1", UserId = "user-1", AgentId = "default"
            }));
            var first = await host.PostStreamAsync(conversationId, message: "Initial OpenCode question");
            Assert.DoesNotContain(first, frame => frame.Event == "error");
            Assert.Single(first, frame => frame.Event == "tool_result");
            Assert.Single(first, frame => frame.Event == "done");
            var second = await host.PostStreamAsync(conversationId, message: "Follow up using Pi", runtimeId: "openagent-pi");
            Assert.DoesNotContain(second, frame => frame.Event is "error" or "tool_call");
            Assert.Contains("Pi", Assert.Single(second, frame => frame.Event == "runtime").Data.GetRawText());
            Assert.Single(second, frame => frame.Event == "done");
            ConversationRecord record = (await host.Store.GetRecordAsync("tenant-1", conversationId))!;
            Assert.Equal("default", record.AgentId);
            Assert.Contains(record.Messages, message => message.Role == "assistant" && message.Content == "Committed OpenCode reply");
            Assert.Contains(record.Messages, message => message.Role == "assistant" && message.Content == "Pi continued the conversation");
            Assert.Equal(3, requests);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [OpenCodeFact]
    public async Task HttpSse_RealOpenCodeCallsPlatformTool_ThenPersistsAndReplaysConversation()
    {
        int mainRequests = 0;
        bool sawHistory = false;
        bool sawNativeHistory = false;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            mainRequests++;
            JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
            if (messages.Any(message => message.GetProperty("role").GetString() == "assistant"
                && message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
                && content.GetString() == "平台工具调用完成"))
            {
                sawHistory = sawNativeHistory = true;
                Assert.DoesNotContain("openagent://conversation/history", request.GetRawText());
                await ModelFixture.TextAsync(context, "已续接历史");
            }
            else if (messages.Any(message => message.GetProperty("role").GetString() == "tool"))
            {
                Assert.Contains("tenant-1", messages.Last(message => message.GetProperty("role").GetString() == "tool").GetRawText());
                await ModelFixture.TextAsync(context, "平台工具调用完成");
            }
            else if (request.GetRawText().Contains("openagent://conversation/history", StringComparison.Ordinal))
            {
                sawHistory = true;
                await ModelFixture.TextAsync(context, "已续接历史");
            }
            else
            {
                string name = request.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)
                    .Single(name => name.EndsWith("get_current_user_profile", StringComparison.Ordinal));
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant",
                    tool_calls = new[] { new { index = 0, id = "profile-1", type = "function", function = new { name, arguments = "{}" } } }
                }, "tool_calls");
            }
        });
        string root = Path.Combine(Path.GetTempPath(), "acp-http-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenCode:Executable"] = Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE"),
                ["OpenCode:WorkspaceRoot"] = root,
                ["OpenCode:TurnTimeoutSeconds"] = "60"
            }).Build();
            // 摘要客户端替身：任何模型推理都说明默认入口走了错误路径。
            await using StreamingHost host = await StreamingHost.StartAsync(
                configure: services => services.AddOpenCodeRuntime(config),
                model: new LlmConfig { ModelId = "fixture", Endpoint = model.Endpoint, ApiKey = "local-test", ContextTokens = 100000 });
            var first = await host.PostStreamAsync("real-acp-conversation");
            Assert.DoesNotContain(first, frame => frame.Event == "error");
            string callId = Assert.Single(first, frame => frame.Event == "tool_call").Data.GetProperty("toolCallId").GetString()!;
            Assert.Equal(callId, Assert.Single(first, frame => frame.Event == "tool_result").Data.GetProperty("toolCallId").GetString());
            Assert.Single(first, frame => frame.Event == "done");
            ConversationRecord record = (await host.Store.GetRecordAsync("tenant-1", "real-acp-conversation"))!;
            Assert.Equal(ConversationStatus.Completed, record.Status);
            Assert.Contains(record.Messages, message => message.Role == "assistant" && message.Content == "平台工具调用完成");
            // 版本无关：持久化 usage 只允许是上下文占用（prompt=total、completion=0），
            // 不伪造计费拆分；不上报 usage_update 的版本保持 null。
            Assert.DoesNotContain(record.Messages, message => message.TokenUsage is { } usage
                && !(usage.CompletionTokens == 0 && usage.PromptTokens == usage.TotalTokens && usage.PromptTokens >= 0));
            var second = await host.PostStreamAsync("real-acp-conversation");
            Assert.DoesNotContain(second, frame => frame.Event == "error");
            Assert.True(sawHistory);
            Assert.True(sawNativeHistory);
            Assert.Equal(3, mainRequests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class OpenCodeAndPiFactAttribute : OpenCodeFactAttribute
    {
        public OpenCodeAndPiFactAttribute()
        {
            if (Skip is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAGENT_TEST_PI_ACP")))
                Skip = "Set OPENAGENT_TEST_PI_ACP to the pinned Pi ACP executable.";
        }
    }
}
