using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Security;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class RuntimeBoundaryTests
{
    [Fact]
    public async Task Runtime_DisabledProviderFailsBeforeAnyProcessStarts()
    {
        ServiceCollection services = new();
        services.AddOpenCodeRuntime(new ConfigurationBuilder().Build());
        await using ServiceProvider provider = services.BuildServiceProvider();
        RuntimeInvocation invocation = new(new AgentRuntimeProfile
        {
            AgentId = "agent", Config = new(), Model = new(),
            Runtime = new RuntimeProfile { Provider = "ClaudeCode", Name = "disabled", Id = "disabled" }
        }, new TurnContext { TenantId = "tenant", UserId = "user", ConversationId = "c", TraceId = "t" }, "", [new("user", "hello")], []);
        IAgentRuntimeDriver driver = provider.GetRequiredService<IAgentRuntimeDriver>();
        AgentException error = await Assert.ThrowsAsync<AgentException>(async () =>
        {
            await foreach (AgentStreamEvent item in driver.RunAsync(invocation)) { }
        });
        Assert.Equal(AgentErrorCode.InvalidRequest, error.ErrorCode);
    }

    [Fact]
    public void Launch_UsesIsolatedEnvironmentAndDenyByDefaultToolPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "acp-launch-" + Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeInvocation request = new(new AgentRuntimeProfile
            {
                AgentId = "agent",
                Config = new AgentConfig(),
                Model = new LlmConfig { ModelId = "chosen", ApiKey = "selected-secret", Endpoint = "https://example.test/v1", ContextTokens = 100000 }
            }, new TurnContext { TenantId = "tenant", ConversationId = "c", UserId = "u", TraceId = "t" }, "平台指令 {file:/must-not-read} {env:PATH}", [new("user", "hello")], []);
            var launch = OpenCodeProcess.Create(new OpenCodeLaunchSpec("opencode", ["acp"]), request, root);
            Assert.Equal(Acp.Processes.AcpEnvironmentMode.AllowList, launch.EnvironmentMode);
            Assert.Equal("selected-secret", launch.Environment!["OPENAGENT_MODEL_API_KEY"]);
            Assert.DoesNotContain("selected-secret", launch.Environment["OPENCODE_CONFIG_CONTENT"]);
            Assert.DoesNotContain("{file:", launch.Environment["OPENCODE_CONFIG_CONTENT"]);
            Assert.DoesNotContain("{env:PATH}", launch.Environment["OPENCODE_CONFIG_CONTENT"]);
            Assert.Contains("{env:OPENAGENT_MODEL_API_KEY}", launch.Environment["OPENCODE_CONFIG_CONTENT"]);
            Assert.StartsWith(root, launch.Environment["HOME"]);
            using JsonDocument config = JsonDocument.Parse(launch.Environment["OPENCODE_CONFIG_CONTENT"]);
            Assert.Equal("deny", config.RootElement.GetProperty("permission").GetProperty("*").GetString());
            Assert.Equal("allow", config.RootElement.GetProperty("permission").GetProperty("openagent_*").GetString());
            Assert.Equal("openagent/chosen", config.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Mapper_MergesToolPatchesAndEmitsOnlyOneTerminalResult()
    {
        AcpEventMapper mapper = new("openagent_");
        AgentStreamEvent call = Assert.Single(mapper.Map(new ToolCall("id", "Readable title")
        {
            Name = "openagent_echo",
            RawInput = JsonSerializer.SerializeToElement(new { text = "参数" })
        }));
        Assert.Equal("echo", call.ToolName);
        AgentStreamEvent result = Assert.Single(mapper.Map(new ToolCallUpdate("id")
        {
            Name = "openagent_echo",
            Status = "completed",
            RawOutput = AcpJson.Element("结果")
        }));
        Assert.Equal("echo", result.ToolName);
        Assert.Equal("id", result.ToolCallId);
        Assert.Equal("结果", result.Content);
        Assert.Empty(mapper.Map(new ToolCallUpdate("id") { Status = "completed" }));
        Assert.Empty(mapper.Map(new UsageUpdate(100, 1000)));
    }

    [Fact]
    public async Task Bridge_RequiresItsOwnBearerTokenAndRevokesAccessOnCancellation()
    {
        int calls = 0;
        using CancellationTokenSource turn = new();
        RuntimeTool tool = new("echo", "echo", """{"type":"object"}""", (arguments, ct) =>
        {
            calls++;
            return Task.FromResult("桥接结果");
        });
        await using RuntimeToolBridgeHost bridge = await RuntimeToolBridgeHost.StartAsync(CancellationToken.None);
        using HttpClient anonymous = new();
        // 无活动轮次注册时一律拒绝。
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(bridge.Server.Url)).StatusCode);
        using RuntimeToolBridgeHost.BridgeLease lease = bridge.Lease([tool], turn.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(bridge.Server.Url)).StatusCode);
        anonymous.DefaultRequestHeaders.Add("Authorization", "Bearer wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(bridge.Server.Url)).StatusCode);
        using HttpClient authorized = new();
        authorized.DefaultRequestHeaders.Add("Authorization", Assert.Single(bridge.Server.Headers!).Value);
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(bridge.Server.Url!),
            TransportMode = HttpTransportMode.StreamableHttp
        }, authorized));
        Assert.Single(await client.ListToolsAsync());
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?>());
        Assert.False(result.IsError ?? false);
        Assert.Equal(1, calls);
        turn.Cancel();
        Assert.Equal(HttpStatusCode.Unauthorized, (await authorized.GetAsync(bridge.Server.Url)).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Bridge_LeaseRotation_ReplacesToolsAndRevokesPreviousTurn()
    {
        int firstCalls = 0;
        int secondCalls = 0;
        RuntimeTool first = new("first_tool", "first", """{"type":"object"}""", (_, _) => { firstCalls++; return Task.FromResult("one"); });
        RuntimeTool second = new("second_tool", "second", """{"type":"object"}""", (_, _) => { secondCalls++; return Task.FromResult("two"); });
        await using RuntimeToolBridgeHost bridge = await RuntimeToolBridgeHost.StartAsync(CancellationToken.None);
        using RuntimeToolBridgeHost.BridgeLease firstLease = bridge.Lease([first], CancellationToken.None);
        using HttpClient authorized = new();
        authorized.DefaultRequestHeaders.Add("Authorization", Assert.Single(bridge.Server.Headers!).Value);
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(bridge.Server.Url!),
            TransportMode = HttpTransportMode.StreamableHttp
        }, authorized));
        Assert.Equal(["first_tool"], (await client.ListToolsAsync()).Select(tool => tool.Name));
        // 下一轮接管：工具表被替换，旧工具不可见；同一 bearer 仅在活动租约期可用。
        using RuntimeToolBridgeHost.BridgeLease secondLease = bridge.Lease([second], CancellationToken.None);
        Assert.Equal(["second_tool"], (await client.ListToolsAsync()).Select(tool => tool.Name));
        var stale = await client.CallToolAsync("first_tool", new Dictionary<string, object?>());
        Assert.True(stale.IsError);
        Assert.Equal(0, firstCalls);
        var result = await client.CallToolAsync("second_tool", new Dictionary<string, object?>());
        Assert.False(result.IsError ?? false);
        Assert.Equal(1, secondCalls);
        // 租约全部释放后桥继续监听但拒绝调用（进程保活场景）。
        secondLease.Dispose();
        firstLease.Dispose();
        Assert.Equal(HttpStatusCode.Unauthorized, (await authorized.GetAsync(bridge.Server.Url)).StatusCode);
    }
}
