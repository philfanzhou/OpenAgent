using OpenAgent.Runtime.OpenCode;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>进程保活复用：追问轮次命中同会话、同启动身份的空闲进程，冷启动只在窗口外支付。</summary>
public sealed class OpenCodeKeepAliveTests
{
    [Fact]
    public void Identity_IsStableAndSensitiveToLaunchDifferences()
    {
        RuntimeInvocation invocation = Invocation(instructions: "同一指令");
        OpenCodeLaunchSpec spec = new("opencode", ["acp"]);
        string identity = OpenCodeProcess.Identity(spec, invocation, []);
        Assert.Equal(identity, OpenCodeProcess.Identity(spec, invocation, []));
        // 宿主放行的原生工具属于启动身份：改动后不得复用旧权限的保活进程。
        Assert.NotEqual(identity, OpenCodeProcess.Identity(spec, invocation, ["read", "write"]));
        Assert.NotEqual(identity, OpenCodeProcess.Identity(spec, invocation with { Instructions = "指令变了" }, []));
        Assert.NotEqual(identity, OpenCodeProcess.Identity(spec, invocation with
        {
            Profile = new AgentRuntimeProfile
            {
                AgentId = "test",
                Config = new AgentConfig(),
                Model = new LlmConfig { ModelId = "fixture", Endpoint = "https://other.test/v1", ApiKey = "local-test", ContextTokens = 100000, Temperature = 0.7 }
            }
        }, []));
        Assert.NotEqual(identity, OpenCodeProcess.Identity(spec, invocation with { McpServers = [new("extra", "http://127.0.0.1:9/mcp", "http")] }, []));
        RuntimeInvocation withServer = invocation with { McpServers = [new("extra", "http://127.0.0.1:9/mcp", "http")] };
        Assert.NotEqual(OpenCodeProcess.Identity(spec, withServer, []), OpenCodeProcess.Identity(spec,
            withServer with { McpServers = [new("extra", "http://127.0.0.1:10/mcp", "http")] }, []));
    }

    [OpenCodeFact]
    public async Task FollowUpTurn_ReusesKeptProcessAcrossIdleWindow()
    {
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            JsonElement content = request.GetProperty("messages").EnumerateArray()
                .Last(message => message.GetProperty("role").GetString() == "user")
                .GetProperty("content");
            string joined = content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString() ?? string.Empty));
            await ModelFixture.TextAsync(context, joined.Contains("追问") ? "追问已回答" : "首轮已回答");
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        List<AgentStreamEvent> first = await runtime.CollectAsync(runtime.Request);
        Assert.Equal("首轮已回答", Text(first));
        Assert.Equal(1, runtime.Keeper.KeptCount);
        Assert.Equal(0, runtime.Keeper.ReusedCount);
        string workspace = Assert.Single(Directory.GetDirectories(runtime.Root, "workspace", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(Path.Combine(workspace, "marker.txt"), "first turn");
        List<AgentStreamEvent> second = await runtime.CollectAsync(runtime.Request with { Messages = [new("user", "请追问")] });
        Assert.Equal("追问已回答", Text(second));
        Assert.Equal(1, runtime.Keeper.ReusedCount);
        Assert.Equal(1, runtime.Keeper.KeptCount);
        AgentStreamEvent started = Assert.Single(second.Where(item => item.Type == AgentStreamEventType.RuntimeStarted));
        Assert.False(string.IsNullOrWhiteSpace(started.Runtime?.AgentVersion));
        Assert.Equal(workspace, Assert.Single(Directory.GetDirectories(runtime.Root, "workspace", SearchOption.AllDirectories)));
        Assert.Equal("first turn", await File.ReadAllTextAsync(Path.Combine(workspace, "marker.txt")));
        // 宿主释放时销毁保活进程并回收进程主目录。
        await runtime.Provider.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(runtime.Root, "p-*", SearchOption.AllDirectories));
    }

    [OpenCodeFact]
    public async Task DifferentConversationOwner_DoesNotShareKeptProcess()
    {
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) => ModelFixture.TextAsync(context, "ok"));
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        await runtime.CollectAsync(runtime.Request);
        Assert.Equal(1, runtime.Keeper.KeptCount);
        await runtime.CollectAsync(runtime.Request with
        {
            Turn = new TurnContext { TenantId = "tenant", UserId = "user", TraceId = "trace-2", ConversationId = "conversation-2" }
        });
        Assert.Equal(0, runtime.Keeper.ReusedCount);
        Assert.Equal(2, runtime.Keeper.KeptCount);
        await runtime.Provider.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(runtime.Root, "p-*", SearchOption.AllDirectories));
    }

    [OpenCodeFact]
    public async Task FailedTurn_IsNotKeptForReuse()
    {
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("remote-secret-must-not-be-saved");
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        await Assert.ThrowsAsync<AgentException>(() => runtime.CollectAsync(runtime.Request));
        Assert.Equal(0, runtime.Keeper.KeptCount);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task IdleKeptProcess_IsSweptAfterKeepAliveWindow()
    {
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) => ModelFixture.TextAsync(context, "ok"));
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "1");
        await runtime.CollectAsync(runtime.Request);
        Assert.Equal(1, runtime.Keeper.KeptCount);
        for (int attempt = 0; attempt < 100 && runtime.Keeper.KeptCount > 0; attempt++)
        {
            await Task.Delay(200);
        }
        Assert.Equal(0, runtime.Keeper.KeptCount);
        // 出池后的销毁（进程退出 + 主目录删除）在计数归零后异步完成，以目录清理为准。
        for (int attempt = 0; attempt < 100 && Directory.GetDirectories(runtime.Root, "p-*", SearchOption.AllDirectories).Length > 0; attempt++)
        {
            await Task.Delay(200);
        }
        Assert.Empty(Directory.GetDirectories(runtime.Root, "p-*", SearchOption.AllDirectories));
        // 过期后重建进程而不是复用。
        List<AgentStreamEvent> second = await runtime.CollectAsync(runtime.Request);
        Assert.Equal("ok", Text(second));
        Assert.Equal(0, runtime.Keeper.ReusedCount);
    }

    [OpenCodeFact]
    public async Task KeepAliveDisabled_DestroysProcessEachTurn()
    {
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) => ModelFixture.TextAsync(context, "ok"));
        await using RuntimeFixture runtime = new(model.Endpoint);
        await runtime.CollectAsync(runtime.Request);
        await runtime.CollectAsync(runtime.Request);
        Assert.Equal(0, runtime.Keeper.KeptCount);
        Assert.Equal(0, runtime.Keeper.ReusedCount);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task SessionLimit_RetiresProcessInsteadOfReusing()
    {
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) => ModelFixture.TextAsync(context, "ok"));
        await using RuntimeFixture runtime = new(model.Endpoint, settings =>
        {
            settings["OpenCode:KeepAliveSeconds"] = "120";
            settings["OpenCode:MaxSessionsPerProcess"] = "1";
        });
        await runtime.CollectAsync(runtime.Request);
        Assert.Equal(0, runtime.Keeper.KeptCount);
        List<AgentStreamEvent> second = await runtime.CollectAsync(runtime.Request);
        Assert.Equal("ok", Text(second));
        Assert.Equal(0, runtime.Keeper.ReusedCount);
    }

    [OpenCodeFact]
    public async Task FollowUpTurn_RotatesToolsThroughReusedProcess()
    {
        int step = 0;
        string? firstTurnArgument = null;
        string? secondTurnArgument = null;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
            if (messages.Any(message => message.GetProperty("role").GetString() == "tool"))
            {
                await ModelFixture.TextAsync(context, step == 1 ? "first done" : "second done");
                return;
            }
            step++;
            string[] names = request.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray();
            string name = Assert.Single(names, value =>
                value.EndsWith(step == 1 ? "_echo_one" : "_echo_two", StringComparison.Ordinal));
            await ModelFixture.ChunkAsync(context, new
            {
                role = "assistant",
                tool_calls = new[] { new { index = 0, id = "call-" + step, type = "function", function = new { name, arguments = "{\"text\":\"turn-" + step + "\"}" } } }
            }, "tool_calls");
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        RuntimeTool first = new("echo_one", "one", """{"type":"object","properties":{"text":{"type":"string"}}}""",
            (arguments, ct) => { firstTurnArgument = arguments["text"]?.ToString(); return Task.FromResult("first-tool-result"); });
        RuntimeTool second = new("echo_two", "two", """{"type":"object","properties":{"text":{"type":"string"}}}""",
            (arguments, ct) => { secondTurnArgument = arguments["text"]?.ToString(); return Task.FromResult("second-tool-result"); });
        List<AgentStreamEvent> firstTurn = await runtime.CollectAsync(runtime.Request with { Tools = [first] });
        Assert.Equal("first done", Text(firstTurn));
        List<AgentStreamEvent> secondTurn = await runtime.CollectAsync(runtime.Request with { Tools = [second] });
        Assert.Equal("second done", Text(secondTurn));
        Assert.Equal(1, runtime.Keeper.ReusedCount);
        Assert.Equal("turn-1", firstTurnArgument);
        Assert.Equal("turn-2", secondTurnArgument);
        Assert.Single(Directory.GetDirectories(runtime.Root, "workspace", SearchOption.AllDirectories));
    }

    [OpenCodeFact]
    public async Task ChangedSkillContent_RebuildsWorkspaceAndKeepsOriginalCopyIndependent()
    {
        string package = Path.Combine(Path.GetTempPath(), "openagent-skill-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        try
        {
            string entry = Path.Combine(package, "SKILL.md");
            await File.WriteAllTextAsync(entry, "---\nname: example\ndescription: Example skill\n---\nFirst version");
            await using ModelFixture model = await ModelFixture.StartAsync((context, request) => ModelFixture.TextAsync(context, "ok"));
            await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
            RuntimeInvocation request = runtime.Request with { Skills = [new("example", package)] };
            await runtime.CollectAsync(request);
            string original = Assert.Single(Directory.GetDirectories(runtime.Root, "workspace", SearchOption.AllDirectories));
            await File.WriteAllTextAsync(entry, "---\nname: example\ndescription: Example skill\n---\nSecond version");
            await runtime.CollectAsync(request);
            Assert.Equal(0, runtime.Keeper.ReusedCount);
            Assert.Contains("First version", await File.ReadAllTextAsync(Path.Combine(original, ".opencode", "skills", "example", "SKILL.md")));
            Assert.Equal(2, Directory.GetDirectories(runtime.Root, "workspace", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [OpenCodeFact]
    public async Task CancelledFollowUp_DestroysAcquiredProcessAndWorkspace()
    {
        int requests = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                await ModelFixture.TextAsync(context, "ok");
                return;
            }
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        await runtime.CollectAsync(runtime.Request with
        {
            HistoryCheckpoint = "initial", CommitAsync = _ => Task.FromResult<string?>("committed")
        });
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(30));
        Task<List<AgentStreamEvent>> followUp = runtime.CollectAsync(runtime.Request with { HistoryCheckpoint = "committed" }, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => followUp.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, runtime.Keeper.ReusedCount);
        Assert.Equal(0, runtime.Keeper.KeptCount);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task CommittedFollowUp_ResumesNativeHistoryAndUsesCurrentToolClosure()
    {
        int turn = 0;
        List<int> invoked = [];
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
            int lastUser = Array.FindLastIndex(messages, message => message.GetProperty("role").GetString() == "user");
            if (messages.Skip(lastUser + 1).Any(message => message.GetProperty("role").GetString() == "tool"))
            {
                await ModelFixture.TextAsync(context, "answer-" + turn);
                return;
            }
            if (turn == 2)
            {
                Assert.Contains(messages, message => message.GetProperty("role").GetString() == "assistant"
                    && message.TryGetProperty("content", out JsonElement content) && content.ToString().Contains("answer-1", StringComparison.Ordinal));
                Assert.DoesNotContain("openagent://conversation/history", request.GetRawText());
            }
            string name = request.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).Single();
            await ModelFixture.ChunkAsync(context, new
            {
                role = "assistant",
                tool_calls = new[] { new { index = 0, id = "echo-" + turn, type = "function", function = new { name, arguments = "{}" } } }
            }, "tool_calls");
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        string checkpoint = "empty";
        for (turn = 1; turn <= 2; turn++)
        {
            int current = turn;
            RuntimeTool tool = new("echo", "Echo", "{\"type\":\"object\",\"properties\":{}}", (_, _) =>
            {
                invoked.Add(current);
                return Task.FromResult("tool-" + current);
            });
            List<AgentStreamEvent> events = await runtime.CollectAsync(runtime.Request with
            {
                Tools = [tool],
                Messages = turn == 1 ? [new("user", "hello")] : [new("user", "hello"), new("assistant", "answer-1"), new("user", "continue")],
                HistoryCheckpoint = checkpoint,
                CommitAsync = _ => Task.FromResult<string?>(checkpoint = "committed-" + current)
            });
            Assert.Equal("answer-" + current, Text(events));
        }
        Assert.Equal([1, 2], invoked);
        Assert.Equal(1, runtime.Keeper.ReusedCount);
    }

    [OpenCodeFact]
    public async Task FailedPlatformCommit_DiscardsNativeSessionAndProcess()
    {
        await using ModelFixture model = await ModelFixture.StartAsync((context, request) => ModelFixture.TextAsync(context, "ok"));
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        await Assert.ThrowsAsync<AgentException>(() => runtime.CollectAsync(runtime.Request with
        {
            HistoryCheckpoint = "initial",
            CommitAsync = _ => throw new InvalidOperationException("Storage rejected commit")
        }));
        Assert.Equal(0, runtime.Keeper.KeptCount);
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task ChangedPlatformCheckpoint_RebuildsNativeContextWithoutOldConversation()
    {
        int turn = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            if (turn == 2)
            {
                Assert.DoesNotContain(request.GetProperty("messages").EnumerateArray(),
                    message => message.GetProperty("role").GetString() == "assistant");
                Assert.Contains("manual-summary", request.GetRawText());
            }
            await ModelFixture.TextAsync(context, "original-answer");
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        for (turn = 1; turn <= 2; turn++)
        {
            await runtime.CollectAsync(runtime.Request with
            {
                HistoryCheckpoint = turn == 1 ? "empty" : "same-version:new-compression",
                Messages = turn == 1 ? [new("user", "hello")] : [new("assistant", "manual-summary"), new("user", "continue")],
                CommitAsync = _ => Task.FromResult<string?>("committed")
            });
        }
        Assert.Equal(1, runtime.Keeper.ReusedCount);
    }

    private static RuntimeInvocation Invocation(string instructions) => new(new AgentRuntimeProfile
    {
        AgentId = "test",
        Config = new AgentConfig(),
        Model = new LlmConfig { ModelId = "fixture", Endpoint = "https://fixture.test/v1", ApiKey = "local-test", ContextTokens = 100000 }
    }, new TurnContext { TenantId = "tenant", UserId = "user", TraceId = "trace", ConversationId = "conversation" },
        instructions, [new("user", "hello")], []);

    private static string Text(IEnumerable<AgentStreamEvent> events) =>
        string.Concat(events.Where(item => item.Type == AgentStreamEventType.Content).Select(item => item.Content));

}
