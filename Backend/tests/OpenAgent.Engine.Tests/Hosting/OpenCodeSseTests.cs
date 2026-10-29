using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAgent.Application.Execution;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Runtime;
using OpenAgent.Runtime.Tests;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class OpenCodeSseTests
{
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
}
