using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Exten;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Hosting;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>
/// 本地进程内端到端验证：真实 Kestrel + 共享异常中间件 + AgentExecutor +
/// AgentStreamWriter 的完整 SSE 链路（无 Postgres/Redis，存储与 LLM 为进程内替身，
/// 位于生产代码相同的 DI 位置）。覆盖三类用户可见缺陷：
/// 1) 工具调用事件缺名字/编号、结果退化为类型名字符串；
/// 2) 客户端停止生成被当作 Error 处理（日志与错误帧污染）；
/// 3) 取消后会话状态错误。
/// </summary>
public sealed class SseToolStreamingTests
{
    private const string TenantId = "tenant-1";
    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = TenantId,
        IsAuthenticated = true
    };

    [Fact]
    public async Task Stream_ToolCallWithoutId_EventsCarryNamePairingAndReadableResult()
    {
        // 提供方不给调用编号（CallId 为空）+ 工具真实执行：
        // tool_call 必须带合成 id 与工具名，tool_result 必须配对同一 id、
        // 结果为可读文本而不是 "Microsoft.Extensions.AI.AIContent[]"。
        var provider = new ScriptedChatClient(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(string.Empty, "get_current_user_profile")])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, "最终回答"),
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 5,
                        OutputTokenCount = 3,
                        TotalTokenCount = 8
                    })])
            ]
        ]);
        await using StreamingHost host = await StreamingHost.StartAsync(provider);

        List<(string Event, JsonElement Data)> frames =
            await host.PostStreamAsync("tool-shape-conversation");

        Assert.Equal("conversation", frames[0].Event);

        (string _, JsonElement callData) = Assert.Single(frames, frame => frame.Event == "tool_call");
        Assert.Equal("get_current_user_profile", callData.GetProperty("toolName").GetString());
        string callId = callData.GetProperty("toolCallId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(callId), "streamed tool call must carry a stable id");

        (string _, JsonElement resultData) = Assert.Single(frames, frame => frame.Event == "tool_result");
        Assert.Equal(callId, resultData.GetProperty("toolCallId").GetString());
        Assert.Equal("get_current_user_profile", resultData.GetProperty("toolName").GetString());
        string? resultContent = resultData.GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(resultContent));
        Assert.DoesNotContain("AIContent", resultContent, StringComparison.Ordinal);

        (string _, JsonElement doneData) = Assert.Single(frames, frame => frame.Event == "done");
        Assert.True(doneData.GetProperty("done").GetBoolean());
        Assert.Equal("tool-shape-conversation", doneData.GetProperty("conversationId").GetString());

        // 回喂模型的下一轮请求里，工具结果也必须是可读文本（空参重试循环的根因位）。
        Assert.Equal(2, provider.Requests.Count);
        FunctionResultContent wireResult = Assert.Single(
            provider.Requests[1].SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>());
        string wire = Assert.IsType<string>(wireResult.Result);
        Assert.DoesNotContain("AIContent", wire, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(wire));
    }

    [Fact]
    public async Task Stream_ClientStopsGeneration_NoErrorFrameNoErrorLog_ConversationCancelled()
    {
        // 用户点击“停止生成”：客户端断开 SSE。旧实现会记一条 Error 级
        // “Unhandled exception mapped to ProblemDetails”，且可能写错误帧。
        var provider = new ScriptedChatClient(
        [
            [new ChatResponseUpdate(ChatRole.Assistant, "部分内容")]
        ],
        holdUntilCancelled: true);
        await using StreamingHost host = await StreamingHost.StartAsync(provider);
        List<string> received = [];
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"{host.Endpoint}/api/v1/agent/chat/stream");
            request.Headers.Add("X-Conversation-Id", "cancel-conversation");
            using HttpResponseMessage response = await host.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            await using Stream stream = await response.Content.ReadAsStreamAsync(
                requestCancellation.Token);
            using var reader = new StreamReader(stream);
            while (!requestCancellation.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(requestCancellation.Token);
                if (line == null)
                {
                    break;
                }
                received.Add(line);
                if (line.Contains("部分内容", StringComparison.Ordinal))
                {
                    break;
                }
            }

            requestCancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            // 读取端随取消抛出属预期。
        }

        using var propagation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await provider.Cancelled.WaitAsync(propagation.Token);
        ConversationRecord? record = null;
        while (record is null or { Status: ConversationStatus.Running })
        {
            propagation.Token.ThrowIfCancellationRequested();
            record = await host.Store.GetRecordAsync(TenantId, "cancel-conversation");
            await Task.Delay(50);
        }

        Assert.Equal(ConversationStatus.Cancelled, record.Status);
        Assert.DoesNotContain(received, line =>
            line.Contains("event: error", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs, entry =>
            entry.Level >= LogLevel.Error &&
            entry.Category.Contains("AgentExceptionHandler", StringComparison.Ordinal));
    }

    internal sealed record CapturedLog(LogLevel Level, EventId EventId, string Category, string Message);

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _logs = new();

        public IReadOnlyList<CapturedLog> Logs => _logs.ToList();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, _logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<CapturedLog> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new CapturedLog(
                    logLevel,
                    eventId,
                    category,
                    formatter(state, exception)));
        }
    }

    /// <summary>进程内 Engine 流式端点宿主：与生产装配相同的组件与中间件顺序。</summary>
    internal sealed class StreamingHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private StreamingHost(
            WebApplication application,
            InMemoryConversationStore store,
            CapturingLoggerProvider loggerProvider)
        {
            _application = application;
            Store = store;
            Logs = loggerProvider.Logs;
            Client = new HttpClient { BaseAddress = new Uri(Endpoint) };
        }

        internal InMemoryConversationStore Store { get; }

        internal HttpClient Client { get; }

        internal IReadOnlyList<CapturedLog> Logs { get; }

        internal string Endpoint => _application.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        public static async Task<StreamingHost> StartAsync(
            ScriptedChatClient provider,
            AgentConfig? agentConfig = null,
            Dictionary<string, string?>? settings = null,
            Action<IServiceCollection>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");

            var capture = new CapturingLoggerProvider();
            builder.Services.AddSingleton<ILoggerProvider>(capture);
            builder.Services.AddSingleton(capture);

            IConfigurationRoot configuration = settings == null
                ? new ConfigurationBuilder().Build()
                : new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            builder.Services.AddSingleton<IConfiguration>(configuration);
            builder.Services.AddSingleton<ICurrentUserContext>(new FixedCurrentUserContext());
            builder.Services.AddAgentCore(configuration);
            // 与生产一致的替身注入位置：运行时解析与 LLM 客户端工厂替换为固定实现。
            builder.Services.RemoveAll<IAgentRuntimeResolver>();
            builder.Services.RemoveAll<AgentRuntimeResolver>();
            builder.Services.AddSingleton<IAgentRuntimeResolver>(
                new StaticRuntimeResolver(agentConfig ?? new AgentConfig { MaxTurns = 4 }));
            builder.Services.RemoveAll<IAgentChatClientFactory>();
            builder.Services.AddSingleton<IAgentChatClientFactory>(new Factory(provider));
            builder.Services.RemoveAll<IConversationStore>();
            builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
            builder.Services.RemoveAll<IFileAssetRepository>();
            builder.Services.AddSingleton<IFileAssetRepository>(new EmptyFileAssetRepository());
            builder.Services.AddAgentErrorHandling();

            // 附加替换位于全部默认注册之后（RemoveAll 类替换才能生效）。
            configure?.Invoke(builder.Services);
            WebApplication application = builder.Build();
            // 本测试宿主环境下 WebHost.UseUrls 会被默认地址（localhost:5000）覆盖，
            // 并发跑多个宿主时必然端口冲突；Build 后显式写 Urls 才稳定生效。
            application.Urls.Clear();
            application.Urls.Add("http://127.0.0.1:0");
            application.UseAgentErrorHandling();
            application.MapPost("/api/v1/agent/chat/stream",
                async (HttpContext context, AgentExecutor executor, CancellationToken ct) =>
            {
                var request = new AgentRequest
                {
                    Query = "hello",
                    AgentId = "default",
                    LlmProfileId = "test-profile",
                    ConversationId = context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
                        ?? Guid.NewGuid().ToString("N"),
                    TraceId = Guid.NewGuid().ToString("N")
                };
                await AgentStreamWriter.WriteSseStreamAsync(
                    context,
                    executor.ExecuteStreamingAsync(request, User, ct),
                    request.TraceId,
                    request.ConversationId,
                    context.RequestServices.GetRequiredService<ILogger<StreamingHost>>(),
                    ct).ConfigureAwait(false);
            });
            await application.StartAsync().ConfigureAwait(false);
            var store = Assert.IsType<InMemoryConversationStore>(
                application.Services.GetRequiredService<IConversationStore>());
            return new StreamingHost(
                application,
                store,
                application.Services.GetRequiredService<CapturingLoggerProvider>());
        }

        internal async Task<List<(string Event, JsonElement Data)>> PostStreamAsync(
            string conversationId)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "/api/v1/agent/chat/stream");
            request.Headers.Add("X-Conversation-Id", conversationId);
            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, $"stream failed: {response.StatusCode}");
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseSse(body);
        }

        private static List<(string Event, JsonElement Data)> ParseSse(string body)
        {
            var frames = new List<(string Event, JsonElement Data)>();
            string? currentEvent = null;
            foreach (string rawLine in body.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    currentEvent = line["event: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal)
                    && currentEvent != null)
                {
                    using JsonDocument document = JsonDocument.Parse(line["data: ".Length..]);
                    frames.Add((currentEvent, document.RootElement.Clone()));
                    currentEvent = null;
                }
            }
            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class StaticRuntimeResolver(AgentConfig config) : IAgentRuntimeResolver
        {
            public Task<AgentRuntimeProfile> ResolveAsync(
                string agentId,
                string llmProfileId,
                IAgentUserContext userContext,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new AgentRuntimeProfile
                {
                    AgentId = agentId,
                    Config = config,
                    Model = new LlmConfig { ModelId = "local-scripted-model" }
                });
        }

        private sealed class Factory(IChatClient provider) : IAgentChatClientFactory
        {
            public IChatClient Create(LlmConfig llm, LlmInteractionCapture? capture = null) =>
                provider;

            public IChatClient CreateSummarizationClient(
                LlmConfig llm,
                ContextPolicy? policy,
                LlmInteractionCapture? capture = null) => provider;
        }

        private sealed class FixedCurrentUserContext : ICurrentUserContext
        {
            public string UserId => User.UserId;
            public string? TenantId => User.TenantId;
            public bool IsAuthenticated => true;
            public IReadOnlyList<string> Roles => [];
            public bool IsInRole(string role) => false;
        }

        private sealed class EmptyFileAssetRepository : IFileAssetRepository
        {
            public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
                Task.FromResult<FileAsset?>(null);

            public Task<IReadOnlyList<FileAsset>> ListReferencedAsync(
                string conversationId,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<FileAsset>>([]);

            public Task EnsureConversationReferencesAsync(
                string conversationId,
                IReadOnlyList<string> fileIds,
                DateTimeOffset createdAt,
                CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<bool> IsReferencedAsync(
                string conversationId,
                string fileId,
                CancellationToken cancellationToken) =>
                Task.FromResult(false);
        }
    }
}
