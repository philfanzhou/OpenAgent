using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Execution.Tools;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Capabilities.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Mcp;

public class McpRoutingTests
{
    [Fact]
    public void McpClientPool_ToolIdentitiesAreIsolatedPerScopeInstance()
    {
        var first = new McpClientPool(new FakeMcpClientFactory(
            new FakeMcpClient(new Dictionary<string, List<McpTool>>())));
        var second = new McpClientPool(new FakeMcpClientFactory(
            new FakeMcpClient(new Dictionary<string, List<McpTool>>())));
        var server = new McpServerConfig
        {
            Name = "Alpha",
            Url = "http://alpha",
            Type = McpServerType.Http
        };

        var identity = first.RegisterTool(server, "lookup");

        Assert.True(first.TryGetTool(identity.RuntimeName, out var binding));
        Assert.Equal("lookup", binding.DisplayName);
        Assert.Equal("Alpha/lookup", binding.ResourceId);
        Assert.Equal(McpServerType.Http, binding.Server.Type);
        Assert.False(second.TryGetTool(identity.RuntimeName, out _));
    }

    [Fact]
    public async Task McpClientPool_KeepsDifferentServerClientsConnectedAndRoutesByIdentity()
    {
        var alphaClient = new FakeMcpClient(new Dictionary<string, List<McpTool>>());
        var betaClient = new FakeMcpClient(new Dictionary<string, List<McpTool>>());
        var pool = new McpClientPool(new SequencedMcpClientFactory(alphaClient, betaClient));
        var alpha = new McpServerConfig { Name = "Alpha", Url = "http://alpha" };
        var beta = new McpServerConfig { Name = "Beta", Url = "http://beta" };

        await pool.GetConnectedClientAsync(alpha, CancellationToken.None);
        await pool.GetConnectedClientAsync(beta, CancellationToken.None);
        var alphaTool = pool.RegisterTool(alpha, "lookup");
        var betaTool = pool.RegisterTool(beta, "lookup");

        await pool.CallToolAsync(alphaTool, new Dictionary<string, object>(), CancellationToken.None);
        await pool.CallToolAsync(betaTool, new Dictionary<string, object>(), CancellationToken.None);

        Assert.True(alphaClient.IsConnected);
        Assert.True(betaClient.IsConnected);
        Assert.Equal("http://alpha", Assert.Single(alphaClient.CallLog).ServerUrl);
        Assert.Equal("http://beta", Assert.Single(betaClient.CallLog).ServerUrl);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMcpServersAreRemoved_DisconnectsActiveClient()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var mcpClient = new FakeMcpClient(new Dictionary<string, List<McpTool>>
        {
            ["http://active"] = [new McpTool { Name = "lookup", Description = "active tool", Schema = "{}" }]
        });
        var config = AgentRunTestFactory.CreateConfig();
        config.Mcp.Servers =
        [
            new() { Name = "Active", Url = "http://active", Type = McpServerType.Http }
        ];
        var run = AgentRunTestFactory.CreateRun(new RecordingEngine(), store, config, mcpClient);
        await run.RunAsync(
            "load mcp",
            AgentRunTestFactory.CreateContext("conv-mcp-active"),
            CancellationToken.None);
        config.Mcp.Servers.Clear();

        await run.RunAsync(
            "remove mcp",
            AgentRunTestFactory.CreateContext("conv-mcp-removed"),
            CancellationToken.None);

        Assert.False(mcpClient.IsConnected);
        Assert.Equal(1, mcpClient.DisconnectCount);
    }

    [Fact]
    public async Task ExecuteAsync_SameUrlWithDifferentTransport_ReconnectsForEachTransport()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var mcpClient = new FakeMcpClient(new Dictionary<string, List<McpTool>>
        {
            ["http://shared"] = [new McpTool { Name = "lookup", Description = "shared tool", Schema = "{}" }]
        });
        var engine = new McpRoutingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        config.Mcp.Servers =
        [
            new() { Name = "Alpha", Url = "http://shared", Type = McpServerType.Http },
            new() { Name = "Beta", Url = "http://shared", Type = McpServerType.SSE }
        ];
        var run = AgentRunTestFactory.CreateRun(engine, store, config, mcpClient);

        await run.RunAsync(
            "use mcp",
            AgentRunTestFactory.CreateContext("conv-mcp-transport"),
            CancellationToken.None);

        Assert.Equal(
            [("http://shared", McpServerType.Http), ("http://shared", McpServerType.SSE)],
            mcpClient.ConnectLog);
    }

    [Fact]
    public async Task ExecuteAsync_RoutesMcpToolToMatchingServer()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var mcpClient = new FakeMcpClient(new Dictionary<string, List<McpTool>>
        {
            ["http://alpha"] = [new McpTool { Name = "alpha_lookup", Description = "alpha tool", Schema = "{}" }],
            ["http://beta"] = [new McpTool { Name = "lookup", Description = "beta tool", Schema = "{}" }]
        });

        var engine = new McpRoutingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        config.Mcp.Servers =
        [
            new() { Name = "Alpha", Url = "http://alpha" },
            new() { Name = "Beta", Url = "http://beta" }
        ];

        var run = AgentRunTestFactory.CreateRun(engine, store, config, mcpClient);

        var result = await run.RunAsync("use mcp", AgentRunTestFactory.CreateContext("conv-mcp"), CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Contains(mcpClient.CallLog, call => call.ServerUrl == "http://beta" && call.ToolName == "lookup");
        Assert.Equal(McpServerType.Http, mcpClient.LastConnectedType);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMcpAliasesCollide_GeneratesUniqueAliases()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var mcpClient = new FakeMcpClient(new Dictionary<string, List<McpTool>>
        {
            ["http://server-a"] = [new McpTool { Name = "lookup", Description = "server a tool", Schema = "{}" }],
            ["http://server-b"] = [new McpTool { Name = "lookup", Description = "server b tool", Schema = "{}" }]
        });

        var engine = new CollidingAliasMcpRoutingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        config.Mcp.Servers =
        [
            new() { Name = "Beta-1", Url = "http://server-a" },
            new() { Name = "Beta 1", Url = "http://server-b" }
        ];

        var run = AgentRunTestFactory.CreateRun(engine, store, config, mcpClient);

        var result = await run.RunAsync("use mcp", AgentRunTestFactory.CreateContext("conv-mcp-alias"), CancellationToken.None);

        Assert.Equal("done", result);
        Assert.NotNull(engine.FirstRequest);

        var mcpToolNames = engine.FirstRequest!.Tools
            .Where(tool => tool.Description.StartsWith("[MCP:", StringComparison.Ordinal))
            .Select(tool => tool.Name)
            .ToList();

        Assert.Equal(2, mcpToolNames.Count);
        Assert.Equal(2, mcpToolNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("mcp__beta_1__lookup", mcpToolNames);
        Assert.Contains("mcp__beta_1__lookup__2", mcpToolNames);
        Assert.Contains(mcpClient.CallLog, call => call.ServerUrl == "http://server-b" && call.ToolName == "lookup");
    }

    [Fact]
    public async Task ExecuteAsync_McpToolCall_LogsSafeRequestResponseAndBindingFields()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var loggerProvider = new CaptureLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var mcpClient = new FakeMcpClient(
            new Dictionary<string, List<McpTool>>
            {
                ["http://beta"] = [new McpTool { Name = "lookup", Description = "beta tool", Schema = "{}" }]
            },
            "customer=contoso; api_key=secret-response-token");

        var engine = new McpRoutingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        config.Mcp.Servers =
        [
            new() { Name = "Beta", Url = "http://beta" }
        ];

        var run = AgentRunTestFactory.CreateRun(engine, store, config, mcpClient, loggerFactory: loggerFactory);
        var context = AgentRunTestFactory.CreateContext("conv-mcp-logging");
        context["AgentId"] = "agent-observability";
        context["TraceId"] = "trace-tool-logging";

        var result = await run.RunAsync("use mcp", context, CancellationToken.None);

        Assert.Equal("done", result);

        var loggerEntries = loggerProvider.Entries;
        var startEntry = Assert.Single(loggerEntries.Where(entry =>
            entry.Properties.TryGetValue("{OriginalFormat}", out var format)
            && string.Equals(format?.ToString(), "Tool call started. ArgumentKeys={ArgumentKeys}, ArgumentSummary={ArgumentSummary}, ArgumentsLength={ArgumentsLength}", StringComparison.Ordinal)));
        Assert.Equal(LogLevel.Information, startEntry.LogLevel);
        Assert.Equal("mcp__beta__lookup", startEntry.ScopeProperties["ToolName"]);
        Assert.Equal("mcp", startEntry.ScopeProperties["ToolType"]);
        Assert.Equal("Beta", startEntry.ScopeProperties["ServerName"]);
        Assert.Equal("http://beta", startEntry.ScopeProperties["ServerUrl"]);
        Assert.Equal("lookup", startEntry.ScopeProperties["BindingToolName"]);
        Assert.Equal("query", startEntry.Properties["ArgumentKeys"]);
        Assert.DoesNotContain("value", startEntry.Properties["ArgumentSummary"]?.ToString(), StringComparison.Ordinal);

        var completedEntry = Assert.Single(loggerEntries.Where(entry =>
            entry.Properties.TryGetValue("{OriginalFormat}", out var format)
            && string.Equals(format?.ToString(), "Tool call completed. Status={Status}, DurationMs={DurationMs}, ResultLength={ResultLength}, ResultSummary={ResultSummary}", StringComparison.Ordinal)));
        Assert.Equal(LogLevel.Information, completedEntry.LogLevel);
        Assert.Equal("success", completedEntry.Properties["Status"]);
        Assert.Equal("Beta", completedEntry.ScopeProperties["ServerName"]);
        Assert.Equal("lookup", completedEntry.ScopeProperties["BindingToolName"]);
        Assert.True(Convert.ToDouble(completedEntry.Properties["DurationMs"]) >= 0);
        Assert.DoesNotContain("secret-response-token", completedEntry.Properties["ResultSummary"]?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=secret-response-token", completedEntry.Message, StringComparison.Ordinal);
    }

}

internal sealed class SequencedMcpClientFactory(params IMcpClient[] clients) : IMcpClientFactory
{
    private readonly Queue<IMcpClient> _clients = new(clients);

    public IMcpClient Create() => _clients.Dequeue();
}
