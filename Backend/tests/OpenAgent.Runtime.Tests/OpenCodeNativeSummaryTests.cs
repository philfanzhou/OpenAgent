using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>
/// 压缩摘要改为直接读会话数据库。用一个记录首个参数再转交真实可执行文件的包装脚本，断言收尾时不再启动 export 进程。
/// </summary>
[Collection(RealOpenCodeCollection.Name)]
public sealed class OpenCodeNativeSummaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acp-native-summary-" + Guid.NewGuid().ToString("N"));
    private readonly string _log;

    public OpenCodeNativeSummaryTests()
    {
        _log = Path.Combine(_root, "launch.log");
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
    }

    [OpenCodeUnixTheory(requiresHarness: true)]
    [InlineData(2)]
    public async Task NativeSummary_OrdinaryTurnsNeverSpawnAnExportProcess(int turns)
    {
        await using ModelFixture model = await ModelFixture.StartAsync((context, _) => ModelFixture.TextAsync(context, "answer"));
        string wrapper = CreateWrapper();

        for (int round = 1; round <= turns; round++)
        {
            await using RuntimeFixture runtime = new(model.Endpoint, config => config["OpenCode:Executable"] = wrapper);
            await runtime.CollectAsync(runtime.Request);
        }

        Assert.Equal(Enumerable.Repeat("acp", turns), File.ReadAllLines(_log));
    }

    [OpenCodeUnixFact(requiresHarness: true)]
    public async Task NativeSummary_CompactionAuditIsReadWithoutAnExportProcess()
    {
        int generation = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            if (IsSummaryRequest(request))
            {
                await ModelFixture.TextAsync(context, "retained-summary-native");
                return;
            }
            // 第一次调用的上下文占用越过年限，OpenCode 在本轮内自动压缩后再回答。
            int tokens = ++generation == 1 ? 95000 : 100;
            await UsageAsync(context, "answer", tokens);
        });
        string wrapper = CreateWrapper();

        // 保活进程，收尾后主目录还在，可以直接检查真实数据库的存储形态。
        await using RuntimeFixture runtime = new(model.Endpoint, config =>
        {
            config["OpenCode:Executable"] = wrapper;
            config["OpenCode:KeepAliveSeconds"] = "60";
        });
        List<AgentStreamEvent> events = await runtime.CollectAsync(runtime.Request);

        AgentStreamEvent? audit = Assert.Single(events, item => item.Type == AgentStreamEventType.Compaction);
        Assert.Equal("Succeeded", audit.Compaction?.Status);
        Assert.Equal("retained-summary-native", audit.Compaction?.Summary);
        Assert.Equal(new[] { "acp" }, File.ReadAllLines(_log));

        // 真实库也必须能直接读出同一条摘要：存储形态一旦变化，这里先于调用记录暴露原因。
        string sessionId = Assert.IsType<string>(audit.Compaction!.RuntimeSessionId);
        string database = Assert.Single(Directory.GetFiles(runtime.Root, "opencode.db", SearchOption.AllDirectories));
        ContextSummary direct = Assert.Single(await OpenCodeSessionDatabase.ReadSummariesAsync(database, sessionId, [], CancellationToken.None));
        Assert.Equal("retained-summary-native", direct.Summary);
    }

    private string CreateWrapper()
    {
        string real = Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE")!;
        string wrapper = Path.Combine(_root, "opencode-launch-log.sh");
        File.WriteAllText(wrapper, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> '" + _log + "'\nexec '" + real + "' \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return wrapper;
    }

    private static bool IsSummaryRequest(JsonElement request) =>
        request.GetProperty("messages").EnumerateArray().Any(message =>
            message.GetProperty("role").GetString() == "system"
            && message.GetProperty("content").ToString().Contains("summar", StringComparison.OrdinalIgnoreCase));

    private static async Task UsageAsync(HttpContext context, string text, int promptTokens)
    {
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new
        {
            id = "local", @object = "chat.completion.chunk", created = 1, model = "fixture",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = 10, total_tokens = promptTokens + 10 }
        }) + "\n\ndata: [DONE]\n\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
