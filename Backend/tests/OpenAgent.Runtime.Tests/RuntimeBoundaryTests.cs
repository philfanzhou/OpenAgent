using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Security;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
        // 桥单例只实现 IAsyncDisposable；容器必须异步释放。
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
        await using RuntimeToolBridge bridge = new();
        await using RuntimeToolLease lease = await bridge.RegisterAsync([tool], turn.Token);
        using HttpClient anonymous = new();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(lease.Server.Url)).StatusCode);
        anonymous.DefaultRequestHeaders.Add("Authorization", "Bearer wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(lease.Server.Url)).StatusCode);
        using HttpClient authorized = new();
        authorized.DefaultRequestHeaders.Add("Authorization", Assert.Single(lease.Server.Headers!).Value);
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(lease.Server.Url!),
            TransportMode = HttpTransportMode.StreamableHttp
        }, authorized));
        Assert.Equal(["echo"], (await client.ListToolsAsync()).Select(item => item.Name));
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?>());
        Assert.False(result.IsError ?? false);
        Assert.Equal(1, calls);
        turn.Cancel();
        Assert.Equal(HttpStatusCode.Unauthorized, (await authorized.GetAsync(lease.Server.Url)).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Bridge_ServesConcurrentTurnsFromOneEndpointWithIsolatedToolsets()
    {
        RuntimeTool first = new("alpha", "first turn tool", """{"type":"object"}""", (_, _) => Task.FromResult("第一轮结果"));
        RuntimeTool second = new("beta", "second turn tool", """{"type":"object"}""", (_, _) => Task.FromResult("第二轮结果"));
        RuntimeTool failing = new("boom", "throws server paths", """{"type":"object"}""", (_, _) => throw new InvalidOperationException("D:/must-not-leak"));
        await using RuntimeToolBridge bridge = new();
        await using RuntimeToolLease one = await bridge.RegisterAsync([first, failing], CancellationToken.None);
        await using RuntimeToolLease two = await bridge.RegisterAsync([second], CancellationToken.None);
        // 共享端点：两轮拿到同一 URL，只有 bearer 不同。
        Assert.Equal(one.Server.Url, two.Server.Url);
        Assert.NotEqual(Assert.Single(one.Server.Headers!).Value, Assert.Single(two.Server.Headers!).Value);
        using HttpClient firstClient = new();
        firstClient.DefaultRequestHeaders.Add("Authorization", Assert.Single(one.Server.Headers!).Value);
        using HttpClient secondClient = new();
        secondClient.DefaultRequestHeaders.Add("Authorization", Assert.Single(two.Server.Headers!).Value);
        await using McpClient firstMcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(one.Server.Url!),
            TransportMode = HttpTransportMode.StreamableHttp
        }, firstClient));
        await using McpClient secondMcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(two.Server.Url!),
            TransportMode = HttpTransportMode.StreamableHttp
        }, secondClient));
        // 各轮只见自己的工具集；交叉调用与回调异常都不泄漏服务端细节。
        Assert.Equal(["alpha", "boom"], (await firstMcp.ListToolsAsync()).Select(item => item.Name).Order());
        Assert.Equal(["beta"], (await secondMcp.ListToolsAsync()).Select(item => item.Name));
        Assert.Equal("第一轮结果", Text(await firstMcp.CallToolAsync("alpha", new Dictionary<string, object?>())));
        Assert.Equal("第二轮结果", Text(await secondMcp.CallToolAsync("beta", new Dictionary<string, object?>())));
        var unknown = await firstMcp.CallToolAsync("beta", new Dictionary<string, object?>());
        Assert.True(unknown.IsError);
        Assert.Equal("Tool not found.", Text(unknown));
        var failure = await firstMcp.CallToolAsync("boom", new Dictionary<string, object?>());
        Assert.True(failure.IsError);
        Assert.Equal("Platform tool execution failed or access was denied.", Text(failure));
        // 释放一轮只吊销该轮 bearer，另一轮不受影响。
        await one.DisposeAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await firstClient.GetAsync(one.Server.Url)).StatusCode);
        Assert.Equal("第二轮结果", Text(await secondMcp.CallToolAsync("beta", new Dictionary<string, object?>())));
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
