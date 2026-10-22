using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Runtime.OpenCode;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>真实 OpenCode + 本地确定性模型；不使用外部凭据，也不消耗付费模型额度。</summary>
public sealed class OpenCodeTests
{
    [OpenCodeFact]
    public async Task TextAndHistory_UseSelectedModelWithoutFabricatingBillingUsage()
    {
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            JsonElement user = request.GetProperty("messages").EnumerateArray().Last(message => message.GetProperty("role").GetString() == "user");
            string[] parts = user.GetProperty("content").EnumerateArray().Select(part => part.GetProperty("text").GetString()!).ToArray();
            string history = parts.First(part => part.StartsWith("[openagent://conversation/history]", StringComparison.Ordinal));
            using JsonDocument previous = JsonDocument.Parse(history[(history.IndexOf('\n') + 1)..]);
            Assert.Equal("之前的决定", previous.RootElement[0].GetProperty("content").GetString());
            Assert.Contains("继续", parts);
            Assert.Equal("Bearer local-test", context.Request.Headers.Authorization.ToString());
            await ModelFixture.TextAsync(context, "你好 ACP");
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        RuntimeInvocation request = runtime.Request with
        {
            Messages = [new("assistant", "之前的决定"), new("user", "继续")]
        };
        List<AgentStreamEvent> events = await runtime.CollectAsync(request);
        Assert.Equal("你好 ACP", Text(events));
        AgentStreamEvent started = Assert.Single(events.Where(item => item.Type == AgentStreamEventType.RuntimeStarted));
        Assert.Equal("OpenCode", started.Runtime?.Provider);
        Assert.False(string.IsNullOrWhiteSpace(started.Runtime?.AgentVersion));
        Assert.Equal("fixture", events.Last().ModelId);
        // 版本无关：harness 上报 usage_update 时终态携带的是上下文占用（prompt=total、completion=0），
        // 不拆分计费；未上报时保持 null。不能断言恒为 null——那只在不上报 usage_update 的版本上成立。
        AgentStreamEvent last = events.Last();
        Assert.True(last.Usage is null
            || (last.Usage.CompletionTokens == 0 && last.Usage.PromptTokens == last.Usage.TotalTokens && last.Usage.PromptTokens >= 0));
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task ToolCall_ExecutesPlatformCallbackAndReturnsItsResultToOpenCode()
    {
        string? invokedArgument = null;
        bool resultReturned = false;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
            JsonElement[] results = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            if (results.Length == 0)
            {
                string[] names = request.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray();
                string name = Assert.Single(names, value => value.EndsWith("_echo", StringComparison.Ordinal));
                Assert.All(names, value => Assert.StartsWith("openagent_", value));
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant",
                    tool_calls = new[] { new { index = 0, id = "echo-1", type = "function", function = new { name, arguments = "{\"text\":\"中文参数\"}" } } }
                }, "tool_calls");
            }
            else
            {
                Assert.Contains("平台工具结果", results[0].GetRawText());
                resultReturned = true;
                await ModelFixture.TextAsync(context, "工具完成");
            }
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        RuntimeTool tool = new("echo", "Echo text", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""",
            (arguments, ct) =>
            {
                invokedArgument = arguments["text"]?.ToString();
                return Task.FromResult("平台工具结果");
            });
        List<AgentStreamEvent> events = await runtime.CollectAsync(runtime.Request with
        {
            Tools = [tool]
        });
        Assert.Equal("中文参数", invokedArgument);
        Assert.True(resultReturned);
        Assert.Equal("工具完成", Text(events));
        AgentStreamEvent call = Assert.Single(events.Where(item => item.Type == AgentStreamEventType.ToolCall)
            .GroupBy(item => item.ToolCallId).Select(group => group.Last()));
        AgentStreamEvent result = Assert.Single(events, item => item.Type == AgentStreamEventType.ToolResult);
        Assert.Equal(call.ToolCallId, result.ToolCallId);
        Assert.Equal(call.ToolName, result.ToolName);
        Assert.Contains("平台工具结果", result.Content);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task Cancellation_ConfirmsPromptCancellationAndReleasesWorkspace()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync();
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(30));
        Task<List<AgentStreamEvent>> run = runtime.CollectAsync(runtime.Request, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task SavedRuntimeTimeout_OverridesLongerHostLimitAndReleasesWorkspace()
    {
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        runtime.Request.Profile.Runtime = new RuntimeProfile { Id = "short", Name = "Short", TurnTimeoutSeconds = 2 };
        AgentException error = await Assert.ThrowsAsync<AgentException>(() =>
            runtime.CollectAsync(runtime.Request).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(AgentErrorCode.LlmTimeout, error.ErrorCode);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task TenantConfigurationMacros_ArePlainInstructionText()
    {
        const string instruction = "Explain {file:/openagent-must-not-read} and {env:PATH} literally.";
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            Assert.Contains(request.GetProperty("messages").EnumerateArray(), message =>
                message.GetProperty("role").GetString() == "system"
                && message.GetProperty("content").GetString()!.Contains(instruction, StringComparison.Ordinal));
            await ModelFixture.TextAsync(context, "literal text");
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        List<AgentStreamEvent> events = await runtime.CollectAsync(runtime.Request with
        {
            Instructions = instruction
        });
        Assert.Equal("literal text", Text(events));
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task ProviderError_DoesNotExposeRemotePayload()
    {
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("remote-secret-must-not-be-saved");
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => runtime.CollectAsync(runtime.Request));
        Assert.DoesNotContain("remote-secret", error.ToString());
        Assert.Null(error.InnerException);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task ProviderAuthError_ClassifiesAsAuthFailureWithoutRawBody()
    {
        // 实测（OpenCode 1.19.3）：401 时 harness 的 RPC 错误 Message 会逐字回带响应体
        // （"Internal error: Incorrect API key provided: sk-***"）。平台侧契约：
        // 分类为 LlmAuthFailed、给出可执行建议、原文与 InnerException 均不得存在。
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) =>
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"error\":{\"message\":\"Incorrect API key provided: sk-probe***\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}");
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => runtime.CollectAsync(runtime.Request));
        Assert.Equal(AgentErrorCode.LlmAuthFailed, error.ErrorCode);
        Assert.Contains("API Key", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sk-probe", error.ToString());
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task ProviderContextOverflow_ClassifiesAsContextOverflow()
    {
        // 实测：OpenCode 把 provider 的 context_length_exceeded 改写为
        // "Session too large to compact - context exceeds model limit ..." 后回传，
        // 分类必须按 harness 文案形态识别，而非只认 provider 原生错误码。
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) =>
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"error\":{\"message\":\"This model's maximum context length is 8192 tokens. However, your messages resulted in 10000 tokens\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}");
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => runtime.CollectAsync(runtime.Request));
        Assert.Equal(AgentErrorCode.LlmContextOverflow, error.ErrorCode);
        Assert.Contains("新会话", error.Message);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task SkillsAndExternalMcp_AreLoadedAndCalledByRealHarness()
    {
        bool mcpCalled = false;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using WebApplication mcp = builder.Build();
        mcp.MapPost("/mcp", async (HttpContext context) =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body);
            JsonElement message = document.RootElement;
            if (!message.TryGetProperty("id", out JsonElement id))
            {
                context.Response.StatusCode = 202;
                return;
            }
            object result;
            switch (message.GetProperty("method").GetString())
            {
                case "initialize":
                    result = new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "verification", version = "1" } };
                    break;
                case "tools/list":
                    result = new { tools = new[] { new { name = "verify", description = "Get verification code", inputSchema = new { type = "object", properties = new { } } } } };
                    break;
                case "tools/call":
                    mcpCalled = true;
                    result = new { content = new[] { new { type = "text", text = "MCP-VERIFIED-842" } } };
                    break;
                default:
                    result = new { };
                    break;
            }
            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result });
        });
        await mcp.StartAsync();
        string endpoint = mcp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/mcp";
        string package = Path.Combine(Path.GetTempPath(), "runtime-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "---\nname: local-verification\ndescription: Local verification phrase.\n---\nSKILL-VERIFIED-319\n");
        try
        {
            int step = 0;
            await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
            {
                string name;
                string arguments;
                if (step++ == 0)
                {
                    name = "skill";
                    arguments = "{\"name\":\"local-verification\"}";
                    Assert.Contains("local-verification", request.GetRawText());
                }
                else if (step == 2)
                {
                    Assert.Contains("SKILL-VERIFIED-319", request.GetRawText());
                    name = request.GetProperty("tools").EnumerateArray()
                        .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)
                        .Single(tool => tool.EndsWith("_verify", StringComparison.Ordinal));
                    arguments = "{}";
                }
                else
                {
                    Assert.Contains("MCP-VERIFIED-842", request.GetRawText());
                    await ModelFixture.TextAsync(context, "skills and MCP verified");
                    return;
                }
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant",
                    tool_calls = new[] { new { index = 0, id = "verify-" + step, type = "function", function = new { name, arguments } } }
                }, "tool_calls");
            });
            await using RuntimeFixture runtime = new(model.Endpoint);
            List<AgentStreamEvent> events = await runtime.CollectAsync(runtime.Request with
            {
                Skills = [new("local-verification", package)],
                McpServers = [new("verification", endpoint, "http")]
            });
            Assert.Equal("skills and MCP verified", Text(events));
            Assert.True(mcpCalled);
            runtime.AssertWorkspacesReleased();
            Assert.True(File.Exists(Path.Combine(package, "SKILL.md")));
        }
        finally
        {
            Directory.Delete(package, true);
        }
    }

    private static string Text(IEnumerable<AgentStreamEvent> events) =>
        string.Concat(events.Where(item => item.Type == AgentStreamEventType.Content).Select(item => item.Content));
}

internal sealed class RuntimeFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openagent-runtime-test-" + Guid.NewGuid().ToString("N"));
    internal IAgentRuntimeDriver Driver => _provider.GetRequiredService<IAgentRuntimeDriver>();
    internal OpenCodeProcessKeeper Keeper => _provider.GetRequiredService<OpenCodeProcessKeeper>();
    internal ServiceProvider Provider => _provider;
    internal string Root => _root;
    internal RuntimeInvocation Request
    {
        get;
    }

    internal RuntimeFixture(string endpoint, Action<Dictionary<string, string?>>? configure = null)
    {
        ServiceCollection services = new();
        Dictionary<string, string?> settings = new()
        {
            ["OpenCode:Executable"] = Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE"),
            ["OpenCode:WorkspaceRoot"] = _root,
            ["OpenCode:RequestTimeoutSeconds"] = "30",
            ["OpenCode:TurnTimeoutSeconds"] = "45",
            // 默认每轮销毁进程，保持既有断言语义；保活场景由用例显式开启。
            ["OpenCode:KeepAliveSeconds"] = "0"
        };
        configure?.Invoke(settings);
        services.AddOpenCodeRuntime(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        _provider = services.BuildServiceProvider();
        Request = new RuntimeInvocation(new AgentRuntimeProfile
        {
            AgentId = "test",
            Config = new AgentConfig(),
            Model = new LlmConfig { ModelId = "fixture", Endpoint = endpoint, ApiKey = "local-test", ContextTokens = 100000 }
        }, new TurnContext { TenantId = "tenant", UserId = "user", TraceId = "trace", ConversationId = "conversation" },
            "Answer the user. Use the echo tool when available.", [new("user", "hello")], []);
    }

    internal async Task<List<AgentStreamEvent>> CollectAsync(RuntimeInvocation request, CancellationToken ct = default)
    {
        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent item in Driver.RunAsync(request, ct))
        {
            events.Add(item);
        }
        return events;
    }

    internal void AssertWorkspacesReleased()
    {
        Assert.DoesNotContain(Directory.GetDirectories(_root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(Path.GetDirectoryName(path)) != Path.GetFileName(_root));
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
