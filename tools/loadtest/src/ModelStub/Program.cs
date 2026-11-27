// 确定性模型桩：替代真实 LLM，为压测提供可控制的 OpenAI Chat Completions 流式响应。
// 行为取自 Backend/tests/OpenAgent.Runtime.Tests/LocalModelFixture.cs，去掉了断言部分，并补上：
//   - 监听 0.0.0.0（供 Engine 容器内的多个 OpenCode 进程共用）；
//   - 首包延迟、按 token 速率分块、错误率 / 挂起率 / 慢响应率的运行时开关；
//   - 请求计数（/stats），用于区分用户轮次与 OpenCode 的标题生成等辅助请求。
using System.Text.Json;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://+:8080");
await using WebApplication app = builder.Build();

StubState state = new(StubConfig.FromEnvironment());

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/stats", () => Results.Json(state.Counters.Snapshot(state.Current)));

app.MapPost("/stats/reset", () =>
{
    state.Counters.Reset();
    return Results.NoContent();
});

app.MapPost("/config", async (HttpContext context) =>
{
    using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    state.Current = state.Current.Apply(document.RootElement);
    return Results.Ok(state.Current);
});

app.MapPost("/v1/chat/completions", (HttpContext context) => HandleAsync(context, state));

app.MapFallback((HttpContext context) =>
{
    state.Counters.UnknownRequests.Increment();
    return Results.NotFound(new { error = "unknown path", path = context.Request.Path.Value });
});

await app.RunAsync();

static async Task HandleAsync(HttpContext context, StubState state)
{
    CancellationToken cancellationToken = context.RequestAborted;
    JsonElement root;
    try
    {
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        root = document.RootElement.Clone();
    }
    catch (Exception error) when (error is JsonException or OperationCanceledException or IOException)
    {
        state.Counters.BadRequests.Increment();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // OpenCode 的标题生成是独立的辅助请求，不算受测的用户推理轮次。
    if (IsTitleRequest(root))
    {
        state.Counters.TitleRequests.Increment();
        await WriteChunkAsync(context, new Dictionary<string, object> { ["role"] = "assistant", ["content"] = "Load test title" },
            "stop", Interlocked.Increment(ref state.CompletionIds), cancellationToken);
        return;
    }

    StubConfig config = state.Current;
    state.Counters.MainRequests.Increment();
    state.Counters.InFlight.Increment();
    try
    {
        // 先按 HangRate 判挂起，其余再按 ErrorRate 判错误。
        double roll = Random.Shared.NextDouble();
        if (roll < config.HangRate)
        {
            state.Counters.Hung.Increment();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        if (roll < config.HangRate + config.ErrorRate)
        {
            state.Counters.InjectedErrors.Increment();
            context.Response.StatusCode = config.ErrorStatus;
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"load stub injected failure\",\"type\":\"server_error\"}}", cancellationToken);
            return;
        }

        int ttftMs = config.TtftMs;
        if (Random.Shared.NextDouble() < config.SlowRate)
        {
            ttftMs = config.SlowTtftMs;
            state.Counters.Slow.Increment();
        }

        await StreamAsync(context, config, ttftMs, state, cancellationToken);
        state.Counters.Completed.Increment();
    }
    catch (Exception error) when (error is OperationCanceledException or IOException)
    {
        // 调用方断开：用户取消，或轮次超时后被 Engine 回收。
        state.Counters.Aborted.Increment();
    }
    finally
    {
        state.Counters.InFlight.Decrement();
    }
}

static bool IsTitleRequest(JsonElement root)
{
    return root.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array
        && messages.EnumerateArray().Any(message =>
            message.TryGetProperty("role", out JsonElement role) && role.GetString() == "system"
            && message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
            && (content.GetString() ?? string.Empty)
            .StartsWith("You are a title generator", StringComparison.Ordinal));
}

static async Task StreamAsync(
    HttpContext context,
    StubConfig config,
    int ttftMs,
    StubState state,
    CancellationToken cancellationToken)
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    await context.Response.Body.FlushAsync(cancellationToken);
    if (ttftMs > 0)
    {
        await Task.Delay(ttftMs, cancellationToken);
    }

    long completion = Interlocked.Increment(ref state.CompletionIds);
    await WriteChunkAsync(context, new Dictionary<string, object> { ["role"] = "assistant", ["content"] = Token(0) }, null,
        completion, cancellationToken);
    for (int index = 1; index < config.Tokens; index++)
    {
        if (config.TokenIntervalMs > 0)
        {
            await Task.Delay(config.TokenIntervalMs, cancellationToken);
        }

        await WriteChunkAsync(context, new Dictionary<string, object> { ["content"] = Token(index) }, null, completion,
            cancellationToken);
    }

    await WriteChunkAsync(context, new Dictionary<string, object>(), "stop", completion, cancellationToken);
    if (config.PromptTokens > 0)
    {
        // 带上 usage，让 OpenCode 上报上下文占用（usage_update）；默认不带，与 Provider 未返回 usage 时一致。
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new
        {
            id = $"chatcmpl-load-{completion}",
            @object = "chat.completion.chunk",
            created = 1,
            model = "load-stub",
            choices = Array.Empty<object>(),
            usage = new
            {
                prompt_tokens = config.PromptTokens,
                completion_tokens = config.Tokens,
                total_tokens = config.PromptTokens + config.Tokens
            }
        }) + "\n\n", cancellationToken);
    }

    await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

static string Token(int index) => $"这是负载测试的第{index + 1}个分片，";

static async Task WriteChunkAsync(
    HttpContext context,
    Dictionary<string, object> delta,
    string? finishReason,
    long completion,
    CancellationToken cancellationToken)
{
    object chunk = new
    {
        id = $"chatcmpl-load-{completion}",
        @object = "chat.completion.chunk",
        created = 1,
        model = "load-stub",
        choices = new[] { new { index = 0, delta, finish_reason = finishReason } }
    };
    await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(chunk) + "\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

sealed class StubState(StubConfig initial)
{
    public volatile StubConfig Current = initial;

    public readonly Counters Counters = new();

    public long CompletionIds;
}

sealed record StubConfig
{
    public int TtftMs { get; init; } = 300;

    public int Tokens { get; init; } = 32;

    public int TokenIntervalMs { get; init; } = 25;

    public double ErrorRate { get; init; }

    public int ErrorStatus { get; init; } = 500;

    public double HangRate { get; init; }

    public double SlowRate { get; init; }

    public int SlowTtftMs { get; init; } = 60000;

    /// <summary>最后一个分片携带的 prompt_tokens（0 表示不返回 usage）。</summary>
    public int PromptTokens { get; init; }

    public static StubConfig FromEnvironment() => new()
    {
        TtftMs = Env("STUB_TTFT_MS", 300),
        Tokens = Env("STUB_TOKENS", 32),
        TokenIntervalMs = Env("STUB_TOKEN_INTERVAL_MS", 25),
        ErrorRate = EnvDouble("STUB_ERROR_RATE"),
        HangRate = EnvDouble("STUB_HANG_RATE"),
        SlowRate = EnvDouble("STUB_SLOW_RATE"),
        SlowTtftMs = Env("STUB_SLOW_TTFT_MS", 60000),
        ErrorStatus = Env("STUB_ERROR_STATUS", 500),
        PromptTokens = Env("STUB_PROMPT_TOKENS", 0)
    };

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    private static double EnvDouble(string name) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0d;

    /// <summary>按 POST /config 的 JSON 覆盖出现的字段，未出现的保持不变。</summary>
    public StubConfig Apply(JsonElement root)
    {
        StubConfig next = this;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            int ReadInt(int fallback) => property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out int number) ? number : fallback;
            double ReadDouble(double fallback) => property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDouble(out double number) ? number : fallback;
            switch (property.Name.ToLowerInvariant())
            {
                case "ttftms": next = next with { TtftMs = ReadInt(TtftMs) }; break;
                case "tokens": next = next with { Tokens = Math.Max(1, ReadInt(Tokens)) }; break;
                case "tokenintervalms": next = next with { TokenIntervalMs = ReadInt(TokenIntervalMs) }; break;
                case "errorrate": next = next with { ErrorRate = ReadDouble(ErrorRate) }; break;
                case "errorstatus": next = next with { ErrorStatus = ReadInt(ErrorStatus) }; break;
                case "hangrate": next = next with { HangRate = ReadDouble(HangRate) }; break;
                case "slowrate": next = next with { SlowRate = ReadDouble(SlowRate) }; break;
                case "slowttftms": next = next with { SlowTtftMs = ReadInt(SlowTtftMs) }; break;
                case "prompttokens": next = next with { PromptTokens = Math.Max(0, ReadInt(PromptTokens)) }; break;
                default: break;
            }
        }

        return next;
    }
}

sealed class Counters
{
    private readonly LongCounter _mainRequests = new();
    private readonly LongCounter _titleRequests = new();
    private readonly LongCounter _inFlight = new();
    private readonly LongCounter _completed = new();
    private readonly LongCounter _aborted = new();
    private readonly LongCounter _injectedErrors = new();
    private readonly LongCounter _hung = new();
    private readonly LongCounter _slow = new();
    private readonly LongCounter _badRequests = new();
    private readonly LongCounter _unknownRequests = new();

    public LongCounter MainRequests => _mainRequests;

    public LongCounter TitleRequests => _titleRequests;

    public LongCounter InFlight => _inFlight;

    public LongCounter Completed => _completed;

    public LongCounter Aborted => _aborted;

    public LongCounter InjectedErrors => _injectedErrors;

    public LongCounter Hung => _hung;

    public LongCounter Slow => _slow;

    public LongCounter BadRequests => _badRequests;

    public LongCounter UnknownRequests => _unknownRequests;

    public void Reset()
    {
        _mainRequests.Reset();
        _titleRequests.Reset();
        _completed.Reset();
        _aborted.Reset();
        _injectedErrors.Reset();
        _hung.Reset();
        _slow.Reset();
        _badRequests.Reset();
        _unknownRequests.Reset();
    }

    public Dictionary<string, object> Snapshot(StubConfig config) => new()
    {
        ["mainRequests"] = _mainRequests.Value,
        ["titleRequests"] = _titleRequests.Value,
        ["inFlight"] = _inFlight.Value,
        ["completed"] = _completed.Value,
        ["aborted"] = _aborted.Value,
        ["injectedErrors"] = _injectedErrors.Value,
        ["hung"] = _hung.Value,
        ["slow"] = _slow.Value,
        ["badRequests"] = _badRequests.Value,
        ["unknownRequests"] = _unknownRequests.Value,
        ["config"] = config
    };
}

sealed class LongCounter
{
    private long _value;

    public long Value => Interlocked.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);

    public void Decrement() => Interlocked.Decrement(ref _value);

    public void Reset() => Interlocked.Exchange(ref _value, 0);
}
