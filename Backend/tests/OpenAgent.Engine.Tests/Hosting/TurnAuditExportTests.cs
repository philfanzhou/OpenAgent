extern alias RouterHost;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Engine.Hosting;
using OpenAgent.Runtime;
using OpenTelemetry.Trace;
using Xunit;
using StreamingHost = OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests.StreamingHost;

namespace OpenAgent.Engine.Tests.Hosting;

[Collection("OtelEnvironment")]
public sealed class TurnAuditExportTests
{
    private const string ConversationId = "audit-test-conversation";
    private const string ChatPath = "/api/v1/agent/chat/stream";
    private const string ResumePath = "/api/v1/agent/conversations/" + ConversationId + "/stream";
    private const string ResumeSpan = "GET /api/v1/agent/conversations/{conversationId}/stream";
    private const string ProducerSpan = "OpenAgent.ConversationRun";
    private const int MissingIdentityEvent = 4200;

    [Fact]
    public async Task RouterChat_ExportsEngineAndProducerOnTheForwardedTrace()
    {
        await using OtlpTestCollector collector = await OtlpTestCollector.StartAsync();
        var driver = new ScriptedRuntimeDriver([new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "Reply" }]);
        await using StreamingHost engine = await StartHostAsync(driver, collector.Endpoint, configureApplication: app =>
            app.MapGet("/api/v1/agent/agents", () => new[] { new { agentId = "default", name = "Default" } }));
        await CreateConversationAsync(engine, ConversationId);
        await using var router = new WebApplicationFactory<RouterHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic",
                ["Authentication:AllowDevelopmentAnonymous"] = "false",
                ["RouterSettings:IntentRecognition:Enabled"] = "false",
                ["RouterSettings:Routing:EngineEndpoint"] = engine.Endpoint,
                ["OpenTelemetry:Protocol"] = "http/protobuf",
                ["OpenTelemetry:TracesEndpoint"] = collector.Endpoint
            }));
        });
        using HttpClient client = router.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, ChatPath)
        {
            Content = JsonContent.Create(new ChatRequest { Message = "Audit through Router" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("admin:admin"u8));
        request.Headers.Add("X-Agent-Id", "default");
        request.Headers.Add("X-Conversation-Id", ConversationId);
        string traceId = SetTraceParent(request);

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Router returned {response.StatusCode}: {body}");
        Assert.Contains("event: done", body);
        TracerProvider routerTracer = router.Services.GetRequiredService<TracerProvider>();
        TracerProvider engineTracer = engine.Services.GetRequiredService<TracerProvider>();
        OtlpTracePayload.Span[] spans = [];
        for (int attempt = 0; attempt < 25; attempt++)
        {
            Assert.True(routerTracer.ForceFlush(5000));
            Assert.True(engineTracer.ForceFlush(5000));
            spans = collector.Requests.Where(request => request.Path == "/v1/traces")
                .SelectMany(request => OtlpTracePayload.Read(request.Body)).Where(span => span.TraceId == traceId).ToArray();
            if (spans.Any(span => span.Name == ProducerSpan) && spans.Count(span => span.Name.StartsWith("POST ", StringComparison.Ordinal)) >= 2)
                break;
            await Task.Delay(40);
        }

        OtlpTracePayload.Span producer = Assert.Single(spans, span => span.Name == ProducerSpan);
        Assert.Equal(ConversationId, producer.Tags.GetValueOrDefault(AuditAttributeNames.ConversationId));
        OtlpTracePayload.Span engineRequest = Assert.Single(spans, span => span.SpanId == producer.ParentId);
        Assert.Equal($"POST {ChatPath}", engineRequest.Name);
        OtlpTracePayload.Span forwardParent = Assert.Single(spans, span => span.SpanId == engineRequest.ParentId);
        var routerRequest = forwardParent.Scope == "YARP.ReverseProxy"
            ? Assert.Single(spans, span => span.SpanId == forwardParent.ParentId)
            : forwardParent;
        Assert.Equal("POST /api/v1/agent/chat/{*action}", routerRequest.Name);
    }

    [Fact]
    public async Task ChatStream_ExportsProducerIdentityAndHttpParent_AndTagsBridgeAndReplay()
    {
        await using OtlpTestCollector collector = await OtlpTestCollector.StartAsync();
        ControlledDriver driver = new();
        await using StreamingHost host = await StartHostAsync(driver, collector.Endpoint);
        await CreateConversationAsync(host, ConversationId);
        using HttpClient client = new(new SocketsHttpHandler { ActivityHeadersPropagator = null })
        {
            BaseAddress = host.Client.BaseAddress
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpRequestMessage request = new(HttpMethod.Post, ChatPath)
        {
            Content = JsonContent.Create(new ChatRequest { Message = "Audit test" })
        };
        request.Headers.Add("X-Conversation-Id", ConversationId);
        string chatTrace = SetTraceParent(request);
        using HttpRequestMessage replayRequest = new(HttpMethod.Get, ResumePath);
        string replayTrace = SetTraceParent(replayRequest);
        try
        {
            using HttpResponseMessage initial = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            initial.EnsureSuccessStatusCode();
            (TurnContext turn, Activity? producerActivity) = await driver.Turn.Task.WaitAsync(timeout.Token);
            await CallBridgeAsync(host, turn, producerActivity, timeout.Token);
            using HttpResponseMessage replay = await client.SendAsync(replayRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            replay.EnsureSuccessStatusCode();
            driver.Finish.TrySetResult();
            Assert.Contains("event: done", await initial.Content.ReadAsStringAsync(timeout.Token));
            Assert.Contains("event: done", await replay.Content.ReadAsStringAsync(timeout.Token));
        }
        finally
        {
            driver.Finish.TrySetResult();
        }

        OtlpTracePayload.Span[] spans = await ReadSpansAsync(collector, host.Services.GetRequiredService<TracerProvider>(), chatTrace, replayTrace);
        var producers = spans.Where(span => span.Name == ProducerSpan).ToArray();
        Assert.True(producers.Count(span => span.TraceId == chatTrace) == 1,
            $"Expected one {ProducerSpan} on trace {chatTrace}. Exported producer traces: {string.Join(", ", producers.Select(span => span.TraceId))}.");
        OtlpTracePayload.Span producer = producers.Single(span => span.TraceId == chatTrace);
        Assert.Equal("OpenAgent.Engine", producer.Scope);
        Assert.Equal(ConversationId, producer.Tags.GetValueOrDefault(AuditAttributeNames.ConversationId));
        Assert.Equal("user-1", producer.Tags.GetValueOrDefault(AuditAttributeNames.UserId));
        OtlpTracePayload.Span http = Assert.Single(spans, span => span.Name == $"POST {ChatPath}" && span.TraceId == chatTrace);
        Assert.Equal(http.TraceId, producer.TraceId);
        Assert.Equal(http.SpanId, producer.ParentId);
        OtlpTracePayload.Span resumed = Assert.Single(spans, span => span.Name == ResumeSpan && span.TraceId == replayTrace);
        Assert.Equal(ConversationId, resumed.Tags.GetValueOrDefault(AuditAttributeNames.ConversationId));
        OtlpTracePayload.Span bridge = Assert.Single(spans, span => span.Name.StartsWith("POST /runtime-tools", StringComparison.Ordinal)
            && span.Tags.GetValueOrDefault("openagent.tool.name") == "audit_echo");
        Assert.Equal("audit_echo", bridge.Tags["openagent.tool.name"]);
        Assert.Equal(ConversationId, bridge.Tags.GetValueOrDefault(AuditAttributeNames.ConversationId));
        Assert.Equal(producer.TraceId, bridge.TraceId);
        Assert.Equal(producer.SpanId, bridge.ParentId);
        OtlpTracePayload.Span child = Assert.Single(spans, span => span.Name == "OpenAgent.RuntimeTool.Invoke"
            && span.ParentId == bridge.SpanId);
        Assert.Equal("audit_echo", child.Tags.GetValueOrDefault("openagent.tool.name"));
        Assert.False(child.Tags.ContainsKey("langfuse.observation.type"));
        foreach (string name in new[] { "audit_native", "audit_external" })
        {
            OtlpTracePayload.Span tool = Assert.Single(spans, span => span.Name == name && span.TraceId == chatTrace);
            Assert.Equal("OpenAgent.Engine", tool.Scope);
            Assert.Equal(producer.SpanId, tool.ParentId);
            Assert.Equal(ConversationId, tool.Tags.GetValueOrDefault(AuditAttributeNames.ConversationId));
        }
        Assert.DoesNotContain(spans, span => span.Name == "audit_echo" && span.TraceId == chatTrace);
        Assert.DoesNotContain(host.Logs, log => log.EventId.Id == MissingIdentityEvent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatStream_WithoutProducerSpan_WarnsOnceOnlyWhenTraceExportIsConfigured(bool traceConfigured)
    {
        await using OtlpTestCollector collector = await OtlpTestCollector.StartAsync();
        ScriptedRuntimeDriver driver = new([new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "Reply" }]);
        await using StreamingHost host = await StartHostAsync(driver, collector.Endpoint,
            traceConfigured, source: "test-unsubscribed-conversation-runs");

        foreach (string conversation in new[] { "first-audit-turn", "second-audit-turn" })
        {
            await CreateConversationAsync(host, conversation);
            var frames = await host.PostStreamAsync(conversation, message: "Audit test");
            Assert.DoesNotContain(frames, frame => frame.Event == "error");
            Assert.Single(frames, frame => frame.Event == "done");
        }

        var warnings = host.Logs.Where(log => log.EventId.Id == MissingIdentityEvent).ToArray();
        if (traceConfigured)
            Assert.Equal(LogLevel.Warning, Assert.Single(warnings).Level);
        else
            Assert.Empty(warnings);
    }

    [Fact]
    public async Task FailedTraceExport_RecordsCounterAndWarningWithoutFailingChat()
    {
        long failures = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "openagent.trace.export.failures") owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();
        await using OtlpTestCollector collector = await OtlpTestCollector.StartAsync(traceStatusCode: 403);
        var driver = new ScriptedRuntimeDriver([new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "Reply" }]);
        await using StreamingHost host = await StartHostAsync(driver, collector.Endpoint);
        await CreateConversationAsync(host, "failed-export");

        var frames = await host.PostStreamAsync("failed-export", message: "Audit failure test");
        Assert.Single(frames, frame => frame.Event == "done");
        Assert.DoesNotContain(frames, frame => frame.Event == "error");
        Assert.True(host.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
        Assert.Contains(collector.Requests, request => request.Path == "/v1/traces");
        Assert.True(Interlocked.Read(ref failures) > 0);
        Assert.Equal(LogLevel.Warning, Assert.Single(host.Logs, log => log.EventId.Id == 4080).Level);
    }

    private static string SetTraceParent(HttpRequestMessage request)
    {
        string traceId = ActivityTraceId.CreateRandom().ToHexString();
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        return traceId;
    }

    private static async Task<OtlpTracePayload.Span[]> ReadSpansAsync(OtlpTestCollector collector, TracerProvider tracer,
        string chatTrace, string replayTrace)
    {
        OtlpTracePayload.Span[] spans = [];
        // The response can finish just before its HTTP and background producer spans end.
        for (int attempt = 0; attempt < 25; attempt++)
        {
            Assert.True(tracer.ForceFlush(5000));
            spans = collector.Requests.Where(request => request.Path == "/v1/traces")
                .SelectMany(request => OtlpTracePayload.Read(request.Body)).ToArray();
            if (spans.Any(span => span.Name == ProducerSpan && span.TraceId == chatTrace)
                && spans.Any(span => span.Name == ResumeSpan && span.TraceId == replayTrace)
                && spans.Any(span => span.Name == $"POST {ChatPath}" && span.TraceId == chatTrace)
                && spans.Any(span => span.Name.StartsWith("POST /runtime-tools", StringComparison.Ordinal)
                    && span.Tags.GetValueOrDefault("openagent.tool.name") == "audit_echo")
                && spans.Any(span => span.Name == "OpenAgent.RuntimeTool.Invoke" && span.TraceId == chatTrace)
                && spans.Any(span => span.Name == "audit_native" && span.TraceId == chatTrace)
                && spans.Any(span => span.Name == "audit_external" && span.TraceId == chatTrace))
            {
                break;
            }
            await Task.Delay(40);
        }
        return spans;
    }

    private static async Task CreateConversationAsync(StreamingHost host, string conversationId)
    {
        Assert.True(await host.Store.CreateAsync(new ConversationRecord
        {
            ConversationId = conversationId, TenantId = "tenant-1", UserId = "user-1", AgentId = "default"
        }));
    }

    private static Task<StreamingHost> StartHostAsync(IAgentRuntimeDriver driver, string endpoint,
        bool traceConfigured = true, string source = "OpenAgent.Engine", Action<WebApplication>? configureApplication = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenTelemetry:Protocol"] = "http/protobuf",
            ["OpenTelemetry:TracesEndpoint"] = traceConfigured ? endpoint : null,
            ["OpenTelemetry:LogsEndpoint"] = endpoint,
            ["OpenTelemetry:MetricsEndpoint"] = endpoint
        }).Build();
        return StreamingHost.StartAsync(driver, configure: services =>
        {
            services.AddAgentHost(configuration, options =>
            {
                options.EnableCors = false;
                options.EnableSwagger = false;
                options.EnableJwtAuth = false;
                options.EnableHealthChecks = false;
                options.OpenTelemetrySource = source;
            });
            services.AddOpenCodeRuntime(configuration);
        }, configureApplication: app =>
        {
            app.MapRuntimeToolBridge();
            configureApplication?.Invoke(app);
        }, useConversationRuns: true);
    }

    private static async Task CallBridgeAsync(StreamingHost host, TurnContext turn, Activity? producer, CancellationToken cancellationToken)
    {
        RuntimeTool tool = new("audit_echo", "Echo", """{"type":"object"}""", (_, _) => Task.FromResult("audited"));
        RuntimeToolBridge bridge = host.Services.GetRequiredService<RuntimeToolBridge>();
        Activity? previous = Activity.Current;
        RuntimeToolLease registered;
        try
        {
            Activity.Current = producer;
            registered = await bridge.RegisterAsync([tool], cancellationToken, turn.ConversationId!, turn.UserId, "test-user", turn.AgentId!);
        }
        finally
        {
            Activity.Current = previous;
        }
        await using RuntimeToolLease lease = registered;
        using HttpClient http = new(new SocketsHttpHandler { ActivityHeadersPropagator = null });
        foreach (var header in lease.Server.Headers!)
            http.DefaultRequestHeaders.Add(header.Name, header.Value);
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(lease.Server.Url!),
            TransportMode = HttpTransportMode.StreamableHttp
        }, http), cancellationToken: cancellationToken);
        var result = await client.CallToolAsync(tool.Name, cancellationToken: cancellationToken);
        Assert.False(result.IsError ?? false);
        Assert.Equal("audited", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private sealed class ControlledDriver : IAgentRuntimeDriver
    {
        internal TaskCompletionSource<(TurnContext, Activity?)> Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation invocation,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Turn.TrySetResult((invocation.Turn, Activity.Current));
            yield return new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "Reply" };
            await Finish.Task.WaitAsync(cancellationToken);
            foreach (string name in new[] { "audit_native", "audit_external", "audit_echo" })
            {
                AgentStreamEvent call = new() { Type = AgentStreamEventType.ToolCall, ToolName = name, ToolCallId = name, IsBridgedTool = name == "audit_echo" };
                yield return call;
                yield return call with { Type = AgentStreamEventType.ToolResult, Content = "tool result" };
            }
        }
    }
}
