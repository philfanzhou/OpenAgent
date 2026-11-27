// OpenAgent 负载发生器：对 /api/v1/agent/chat/stream（SSE）与只读接口施加可控负载，逐请求记录分段时延。
//
// 之所以自写而不用现成压测框架：本项目的核心指标是 SSE 流内各事件（conversation / runtime /
// 首个 content / done）的到达时刻，以及“收到第 N 个 content 后断开”这类按事件触发的动作，
// 通用框架的 HTTP 客户端都不暴露流式读取的时机。全部行为由环境变量配置，结果写到 OUT_DIR：
//   requests-<LABEL>.csv   每个请求一行，时延均为相对该请求发出的毫秒数
//   summary-<LABEL>.json   配置快照与按结果的计数
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

string label = Cfg.Str("LABEL", "run");
string outDir = Cfg.Str("OUT_DIR", "/out");
Directory.CreateDirectory(outDir);
ThreadPool.SetMinThreads(Math.Max(Cfg.Int("CLIENT_MIN_THREADS", 512), Environment.ProcessorCount * 4), 512);

RunOptions options = new();
Console.WriteLine($"== OpenAgent load driver: scenario={options.Scenario} model={options.Model} target={options.BaseUrl}");
Console.WriteLine("== options " + JsonSerializer.Serialize(options));

using SocketsHttpHandler handler = new()
{
    UseProxy = false,
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    EnableMultipleHttp2Connections = true
};
using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

await PreflightAsync(client, options, stop.Token);

Driver driver = new(client, options, outDir, label, stop);
await driver.RunAsync(stop.Token);
Console.WriteLine("== done");

static async Task PreflightAsync(HttpClient client, RunOptions options, CancellationToken cancellationToken)
{
    using HttpResponseMessage health = await client.GetAsync(options.BaseUrl + "/health/ready", cancellationToken);
    Console.WriteLine($"== preflight /health/ready -> {(int)health.StatusCode}");
    if (!health.IsSuccessStatusCode)
    {
        throw new InvalidOperationException("target is not ready");
    }

    if (options.Op != "chat")
    {
        return;
    }

    using HttpRequestMessage agents = new(HttpMethod.Get, options.BaseUrl + "/api/v1/agent/agents");
    if (options.Auth.Length > 0)
    {
        agents.Headers.TryAddWithoutValidation("Authorization", options.Auth);
    }

    using HttpResponseMessage response = await client.SendAsync(agents, cancellationToken);
    string body = await response.Content.ReadAsStringAsync(cancellationToken);
    Console.WriteLine($"== preflight /api/v1/agent/agents -> {(int)response.StatusCode} {Cfg.Truncate(body, 400)}");
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException("agent catalog is not available");
    }
}

internal static class Cfg
{
    public static string Str(string key, string fallback = "") =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;

    public static int Int(string key, int fallback) =>
        int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    public static double Dbl(string key, double fallback) =>
        double.TryParse(Str(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    public static bool Flag(string key) => Str(key).ToLowerInvariant() is "1" or "true" or "yes";

    public static string Truncate(string? text, int max) =>
        text is null ? string.Empty : text.Length <= max ? text : text[..max] + "...";
}

internal sealed class RunOptions
{
    public string Scenario { get; init; } = Cfg.Str("SCENARIO", "cold");

    /// <summary>closed：固定并发循环；open：固定到达率；ramp：逐级并发；burst：一次性齐发。</summary>
    public string Model { get; init; } = Cfg.Str("MODEL", "closed");

    public string BaseUrl { get; init; } = Cfg.Str("TARGET", "http://engine:5208").TrimEnd('/');

    public string Auth { get; init; } = Cfg.Str("AUTH",
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Cfg.Str("USER", "admin") + ":" + Cfg.Str("PASSWORD", "admin"))));

    public string AgentId { get; init; } = Cfg.Str("AGENT_ID", "default");

    public string LlmProfileId { get; init; } = Cfg.Str("LLM_PROFILE_ID", "load-stub");

    public string RuntimeId { get; init; } = Cfg.Str("RUNTIME_ID");

    public string Message { get; init; } = Cfg.Str("MESSAGE", "你好，请用一句话介绍你自己。");

    /// <summary>new：每个请求新会话；worker：每个 worker 复用自己的会话；shared：全部使用同一会话。</summary>
    public string ConvMode { get; init; } = Cfg.Str("CONV_MODE", "new");

    public string SharedConversationId { get; init; } = Cfg.Str("CONVERSATION_ID");

    /// <summary>chat：发一轮流式聊天；get：请求 GET_PATH（可含 {conv} 占位符）。</summary>
    public string Op { get; init; } = Cfg.Str("OP", "chat");

    public string GetPath { get; init; } = Cfg.Str("GET_PATH", "/api/v1/agent/conversations?skip=0&take=20");

    public string[] ConversationIds { get; init; } = Cfg.Str("CONV_IDS")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int Concurrency { get; init; } = Cfg.Int("CONCURRENCY", 8);

    public int DurationSec { get; init; } = Cfg.Int("DURATION_SEC", 60);

    public int ThinkMs { get; init; } = Cfg.Int("THINK_MS", 0);

    public int WarmupSec { get; init; } = Cfg.Int("WARMUP_SEC", 0);

    public double Rate { get; init; } = Cfg.Dbl("RATE", 10);

    public bool Poisson { get; init; } = Cfg.Flag("POISSON");

    public int MaxInflight { get; init; } = Cfg.Int("MAX_INFLIGHT", 512);

    public (int Concurrency, int Seconds)[] Ramp { get; init; } = ParseRamp(Cfg.Str("RAMP", "1:30,2:30,4:30,8:60,16:60,32:60"));

    public int SettleSec { get; init; } = Cfg.Int("SETTLE_SEC", 15);

    public int BurstSize { get; init; } = Cfg.Int("BURST_SIZE", 50);

    public int BurstRounds { get; init; } = Cfg.Int("BURST_ROUNDS", 1);

    public int BurstGapSec { get; init; } = Cfg.Int("BURST_GAP_SEC", 10);

    /// <summary>收到第 N 个 content 后中止本轮（0 表示不中止）。</summary>
    public int AbortAfterContent { get; init; } = Cfg.Int("ABORT_AFTER_CONTENT", 0);

    /// <summary>true：调用 /cancel 接口并继续读到 done；false：直接断开连接。</summary>
    public bool CancelApi { get; init; } = Cfg.Flag("CANCEL_API");

    public bool SendConversationHeader { get; init; } = !Cfg.Flag("NO_CONVERSATION_HEADER");

    public int RequestTimeoutSec { get; init; } = Cfg.Int("REQUEST_TIMEOUT_SEC", 600);

    /// <summary>整个运行最多发起的请求数（0 表示不限）。</summary>
    public int MaxRequests { get; init; } = Cfg.Int("MAX_REQUESTS", 0);

    /// <summary>open / burst 模型下，调度结束后最多再等这么久；仍未完成的请求由客户端主动放弃（0 表示一直等）。</summary>
    public int DrainSec { get; init; } = Cfg.Int("DRAIN_SEC", 0);

    private static (int Concurrency, int Seconds)[] ParseRamp(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split(':', StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
            .Select(parts => (Concurrency: int.Parse(parts[0], CultureInfo.InvariantCulture),
                Seconds: int.Parse(parts[1], CultureInfo.InvariantCulture)))
            .ToArray();
}

internal sealed class WorkerState(int id)
{
    public int Id { get; } = id;

    public int Iteration;

    public string? ConversationId;
}

internal sealed record ChatCall(int Worker, int Iteration, string ConversationId, bool Reused);

internal sealed class Result
{
    public string Phase = string.Empty;
    public int Worker;
    public int Iteration;
    public double StartMs;
    public string ConversationId = string.Empty;
    public bool Reused;
    public double? THeadersMs;
    public double? TFirstByteMs;
    public double? TFirstEventMs;
    public double? TConversationMs;
    public double? TRuntimeMs;
    public double? TFirstReasoningMs;
    public double? TFirstTokenMs;
    public double? TDoneMs;
    public int StatusCode;
    public string Outcome = string.Empty;
    public string DoneStatus = string.Empty;
    public int CancelStatus;
    public string Detail = string.Empty;
    public int Events;
    public int ContentEvents;
    public int ToolCalls;
    public long Bytes;
    public string TraceId = string.Empty;

    public static string CsvHeader() => string.Join(',',
        "phase", "worker", "iteration", "start_ms", "conv", "reused",
        "t_headers_ms", "t_first_byte_ms", "t_first_event_ms", "t_conversation_ms", "t_runtime_ms", "t_first_reasoning_ms",
        "t_first_token_ms", "t_done_ms", "status_code", "outcome", "done_status", "cancel_status", "detail",
        "events", "content_events", "tool_calls", "bytes", "trace_id");

    public string Csv()
    {
        static string Num(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
        string detail = Detail.Contains(',') || Detail.Contains('"') ? "\"" + Detail.Replace('"', '\'') + "\"" : Detail;
        string[] columns =
        [
            Phase, Worker.ToString(CultureInfo.InvariantCulture), Iteration.ToString(CultureInfo.InvariantCulture),
            Num(StartMs), ConversationId, Reused ? "1" : "0",
            THeadersMs is { } headers ? Num(headers) : string.Empty,
            TFirstByteMs is { } firstByte ? Num(firstByte) : string.Empty,
            TFirstEventMs is { } firstEvent ? Num(firstEvent) : string.Empty,
            TConversationMs is { } conversation ? Num(conversation) : string.Empty,
            TRuntimeMs is { } runtime ? Num(runtime) : string.Empty,
            TFirstReasoningMs is { } reasoning ? Num(reasoning) : string.Empty,
            TFirstTokenMs is { } firstToken ? Num(firstToken) : string.Empty,
            TDoneMs is { } done ? Num(done) : string.Empty,
            StatusCode.ToString(CultureInfo.InvariantCulture), Outcome, DoneStatus,
            CancelStatus.ToString(CultureInfo.InvariantCulture), detail,
            Events.ToString(CultureInfo.InvariantCulture), ContentEvents.ToString(CultureInfo.InvariantCulture),
            ToolCalls.ToString(CultureInfo.InvariantCulture), Bytes.ToString(CultureInfo.InvariantCulture), TraceId
        ];
        return string.Join(',', columns);
    }
}

internal sealed class Driver(HttpClient client, RunOptions options, string outDir, string label, CancellationTokenSource stop)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, int> _outcomes = new();
    private readonly ConcurrentDictionary<int, int> _statusCodes = new();
    private readonly ConcurrentDictionary<string, int> _doneStatuses = new();
    private readonly ConcurrentDictionary<string, int> _details = new();
    private readonly object _writeLock = new();
    private readonly string _sharedConversationId = options.SharedConversationId.Length > 0
        ? options.SharedConversationId
        : Guid.NewGuid().ToString("N");
    private StreamWriter _csv = null!;
    private string _phase = "main";
    private int _inFlight;
    private int _finished;
    private int _completed;
    private int _started;
    private int _overload;
    private long _totalBytes;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _csv = new StreamWriter(Path.Combine(outDir, $"requests-{label}.csv"), false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        await _csv.WriteLineAsync(Result.CsvHeader());
        string startedUtc = DateTime.UtcNow.ToString("O");
        double startedMs = _clock.Elapsed.TotalMilliseconds;
        Task progress = ReportProgressAsync(cancellationToken);
        try
        {
            switch (options.Model)
            {
                case "ramp":
                    foreach ((int concurrency, int seconds) in options.Ramp)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        _phase = $"c{concurrency}";
                        Console.WriteLine($"== phase {_phase} ({seconds}s) at {_clock.Elapsed.TotalSeconds:F0}s");
                        await RunClosedAsync(concurrency, _clock.Elapsed.TotalMilliseconds + seconds * 1000.0, cancellationToken);
                        if (options.SettleSec > 0)
                        {
                            Console.WriteLine($"== settle {options.SettleSec}s");
                            await Task.Delay(TimeSpan.FromSeconds(options.SettleSec), cancellationToken);
                        }
                    }

                    break;
                case "open":
                    _phase = "main";
                    await RunOpenAsync(startedMs, _clock.Elapsed.TotalMilliseconds + options.DurationSec * 1000.0, cancellationToken);
                    break;
                case "burst":
                    await RunBurstAsync(cancellationToken);
                    break;
                default:
                    if (options.WarmupSec > 0)
                    {
                        _phase = "warmup";
                        Console.WriteLine($"== phase warmup ({options.WarmupSec}s)");
                        await RunClosedAsync(options.Concurrency, startedMs + options.WarmupSec * 1000.0, cancellationToken);
                    }

                    _phase = "main";
                    await RunClosedAsync(options.Concurrency, startedMs + (options.WarmupSec + options.DurationSec) * 1000.0,
                        cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("== stopped by cancellation");
        }

        Interlocked.Exchange(ref _finished, 1);
        await progress;

        // 各阶段的近似起点（相对运行开始的毫秒），便于把资源采样对齐到阶段。
        Dictionary<string, double> phases = new();
        double offset = startedMs;
        if (options.Model == "ramp")
        {
            foreach ((int concurrency, int seconds) in options.Ramp)
            {
                phases[$"c{concurrency}"] = offset;
                offset += (seconds + options.SettleSec) * 1000.0;
            }
        }
        else
        {
            if (options.WarmupSec > 0)
            {
                phases["warmup"] = offset;
                offset += options.WarmupSec * 1000.0;
            }

            phases["main"] = offset;
        }

        object summary = new
        {
            label,
            startedUtc,
            durationSec = Math.Round((_clock.Elapsed.TotalMilliseconds - startedMs) / 1000.0, 1),
            options,
            completed = _completed,
            overloadSkipped = _overload,
            totalBytes = _totalBytes,
            outcomes = Sorted(_outcomes),
            statusCodes = Sorted(_statusCodes),
            doneStatuses = Sorted(_doneStatuses),
            details = Sorted(_details, 15),
            phaseStartMs = phases
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, $"summary-{label}.json"), JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("== summary " + JsonSerializer.Serialize(new
        {
            completed = _completed,
            outcomes = Sorted(_outcomes),
            statusCodes = Sorted(_statusCodes),
            doneStatuses = Sorted(_doneStatuses)
        }));
    }

    private static Dictionary<string, int> Sorted(ConcurrentDictionary<string, int> source, int limit = int.MaxValue) =>
        source.OrderByDescending(item => item.Value).Take(limit).ToDictionary(item => item.Key, item => item.Value);

    private static Dictionary<string, int> Sorted(ConcurrentDictionary<int, int> source) =>
        source.OrderByDescending(item => item.Value)
            .ToDictionary(item => item.Key.ToString(CultureInfo.InvariantCulture), item => item.Value);

    /// <summary>尝试占用一个请求名额；返回 false 表示已达到 MAX_REQUESTS 或正在停止。</summary>
    private bool TryStart(CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && (options.MaxRequests <= 0 || Interlocked.Increment(ref _started) <= options.MaxRequests);

    private async Task RunClosedAsync(int concurrency, double untilMs, CancellationToken cancellationToken)
    {
        Task[] workers = Enumerable.Range(0, concurrency).Select(async workerId =>
        {
            WorkerState worker = new(workerId);
            while (_clock.Elapsed.TotalMilliseconds < untilMs && TryStart(cancellationToken))
            {
                worker.Iteration++;
                Interlocked.Increment(ref _inFlight);
                try
                {
                    Record(await ExecuteAsync(NextCall(worker), cancellationToken));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }

                if (options.ThinkMs > 0)
                {
                    await Task.Delay(options.ThinkMs, cancellationToken);
                }
            }
        }).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task RunOpenAsync(double phaseStartMs, double untilMs, CancellationToken cancellationToken)
    {
        double intervalMs = 1000.0 / options.Rate;
        Random random = new(20261126);
        List<Task> tasks = new();
        double scheduled = phaseStartMs;
        int sequence = 0;
        while (scheduled < untilMs && TryStart(cancellationToken))
        {
            double waitMs = scheduled - _clock.Elapsed.TotalMilliseconds;
            if (waitMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
            }

            if (Interlocked.Increment(ref _inFlight) > options.MaxInflight)
            {
                Interlocked.Decrement(ref _inFlight);
                Interlocked.Increment(ref _overload);
            }
            else
            {
                int workerId = sequence % 1_000_000;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        Record(await ExecuteAsync(NextCall(new WorkerState(workerId)), cancellationToken));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }
                }, CancellationToken.None));
            }

            sequence++;
            scheduled += options.Poisson ? -Math.Log(1 - random.NextDouble()) * intervalMs : intervalMs;
        }

        await DrainAsync(tasks, cancellationToken);
    }

    /// <summary>超过 DRAIN_SEC 仍未全部完成时取消剩余请求：模拟过载下客户端放弃排队。</summary>
    private async Task DrainAsync(List<Task> tasks, CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        Task all = Task.WhenAll(tasks);
        if (options.DrainSec > 0)
        {
            Task finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(options.DrainSec), cancellationToken));
            if (finished != all)
            {
                Console.WriteLine($"== drain timeout after {options.DrainSec}s: client gives up on in-flight requests");
                stop.Cancel();
            }
        }

        await all;
    }

    private async Task RunBurstAsync(CancellationToken cancellationToken)
    {
        for (int round = 1; round <= options.BurstRounds && !cancellationToken.IsCancellationRequested; round++)
        {
            _phase = $"round{round}";
            Console.WriteLine($"== burst {_phase}: {options.BurstSize} requests at {_clock.Elapsed.TotalSeconds:F0}s");
            string? shared = options.ConvMode == "shared" ? _sharedConversationId : null;
            List<Task> tasks = Enumerable.Range(0, options.BurstSize).Select(index => Task.Run(async () =>
            {
                if (!TryStart(cancellationToken))
                {
                    return;
                }

                WorkerState worker = new(index) { Iteration = 1, ConversationId = shared };
                Interlocked.Increment(ref _inFlight);
                try
                {
                    Record(await ExecuteAsync(NextCall(worker), cancellationToken));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }, CancellationToken.None)).ToList();
            await DrainAsync(tasks, cancellationToken);
            if (round < options.BurstRounds && options.BurstGapSec > 0)
            {
                Console.WriteLine($"== gap {options.BurstGapSec}s");
                await Task.Delay(TimeSpan.FromSeconds(options.BurstGapSec), cancellationToken);
            }
        }
    }

    private ChatCall NextCall(WorkerState worker)
    {
        switch (options.ConvMode)
        {
            case "worker":
                worker.ConversationId ??= Guid.NewGuid().ToString("N");
                return new ChatCall(worker.Id, worker.Iteration, worker.ConversationId, worker.Iteration > 1);
            case "shared":
                return new ChatCall(worker.Id, worker.Iteration, _sharedConversationId, worker.Iteration > 1);
            default:
                return new ChatCall(worker.Id, worker.Iteration, Guid.NewGuid().ToString("N"), false);
        }
    }

    private void Record(Result result)
    {
        Interlocked.Increment(ref _completed);
        _outcomes.AddOrUpdate(result.Outcome, 1, (_, count) => count + 1);
        if (result.StatusCode > 0)
        {
            _statusCodes.AddOrUpdate(result.StatusCode, 1, (_, count) => count + 1);
        }

        if (result.DoneStatus.Length > 0)
        {
            _doneStatuses.AddOrUpdate(result.DoneStatus, 1, (_, count) => count + 1);
        }

        if (result.Outcome != "ok" && result.Detail.Length > 0)
        {
            _details.AddOrUpdate(Cfg.Truncate(result.Detail, 120), 1, (_, count) => count + 1);
        }

        Interlocked.Add(ref _totalBytes, result.Bytes);
        lock (_writeLock)
        {
            _csv.WriteLine(result.Csv());
        }
    }

    private Task<Result> ExecuteAsync(ChatCall call, CancellationToken cancellationToken) =>
        options.Op == "get" ? ExecuteGetAsync(call, cancellationToken) : ExecuteChatAsync(call, cancellationToken);

    private void Authorize(HttpRequestMessage request)
    {
        if (options.Auth.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", options.Auth);
        }
    }

    private async Task<Result> ExecuteGetAsync(ChatCall call, CancellationToken cancellationToken)
    {
        Result result = new()
        {
            Phase = Volatile.Read(ref _phase),
            Worker = call.Worker,
            Iteration = call.Iteration,
            StartMs = _clock.Elapsed.TotalMilliseconds,
            ConversationId = call.ConversationId
        };
        string path = options.GetPath;
        if (path.Contains("{conv}", StringComparison.Ordinal))
        {
            string conversationId = options.ConversationIds.Length > 0
                ? options.ConversationIds[Random.Shared.Next(options.ConversationIds.Length)]
                : call.ConversationId;
            path = path.Replace("{conv}", Uri.EscapeDataString(conversationId), StringComparison.Ordinal);
            result.ConversationId = conversationId;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSec));
        long t0 = Stopwatch.GetTimestamp();
        double ElapsedMs() => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, options.BaseUrl + path);
            Authorize(request);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            result.StatusCode = (int)response.StatusCode;
            result.THeadersMs = ElapsedMs();
            byte[] body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            result.Bytes = body.Length;
            result.TDoneMs = ElapsedMs();
            result.Outcome = response.IsSuccessStatusCode ? "ok" : "http_error";
            if (!response.IsSuccessStatusCode)
            {
                result.Detail = Cfg.Truncate(ProblemTitle(Encoding.UTF8.GetString(body)), 160);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Outcome = "timeout";
            result.Detail = "client timeout";
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "stopped";
        }
        catch (Exception error)
        {
            result.Outcome = "exception";
            result.Detail = Cfg.Truncate(error.GetType().Name + ": " + error.Message, 160);
        }

        result.TDoneMs ??= ElapsedMs();
        return result;
    }

    private async Task<Result> ExecuteChatAsync(ChatCall call, CancellationToken cancellationToken)
    {
        Result result = new()
        {
            Phase = Volatile.Read(ref _phase),
            Worker = call.Worker,
            Iteration = call.Iteration,
            StartMs = _clock.Elapsed.TotalMilliseconds,
            ConversationId = call.ConversationId,
            Reused = call.Reused
        };
        Dictionary<string, object> requestContext = new()
        {
            ["agentId"] = options.AgentId,
            ["llmProfileId"] = options.LlmProfileId,
            ["conversationId"] = call.ConversationId
        };
        if (options.RuntimeId.Length > 0)
        {
            requestContext["runtimeId"] = options.RuntimeId;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, options.BaseUrl + "/api/v1/agent/chat/stream");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { message = options.Message, fileIds = Array.Empty<string>(), context = requestContext }),
            Encoding.UTF8, "application/json");
        Authorize(request);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (options.SendConversationHeader)
        {
            request.Headers.TryAddWithoutValidation("X-Conversation-Id", call.ConversationId);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSec));
        long t0 = Stopwatch.GetTimestamp();
        double ElapsedMs() => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        StreamState state = new();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            result.StatusCode = (int)response.StatusCode;
            result.THeadersMs = ElapsedMs();
            if (response.Headers.TryGetValues("X-Trace-Id", out IEnumerable<string>? traceValues))
            {
                result.TraceId = Cfg.Truncate(traceValues.FirstOrDefault(), 64);
            }

            if (!response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                result.Bytes = text.Length;
                result.Outcome = "http_error";
                result.Detail = Cfg.Truncate(ProblemTitle(text), 160);
                result.TDoneMs = ElapsedMs();
                return result;
            }

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            bool ndjson = string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase);
            await ReadStreamAsync(stream, ndjson, state, result, ElapsedMs, cts).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.AbortTriggered)
        {
            result.Outcome = "client_aborted";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Outcome = "timeout";
            result.Detail = "client timeout after " + options.RequestTimeoutSec + "s";
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "stopped";
        }
        catch (Exception error)
        {
            result.Outcome = "exception";
            result.Detail = Cfg.Truncate(error.GetType().Name + ": " + error.Message, 160);
        }

        if (state.CancelTask is not null)
        {
            result.CancelStatus = await state.CancelTask.ConfigureAwait(false);
        }

        result.TDoneMs ??= ElapsedMs();
        return result;
    }

    private async Task ReadStreamAsync(
        Stream stream,
        bool ndjson,
        StreamState state,
        Result result,
        Func<double> now,
        CancellationTokenSource cts)
    {
        byte[] buffer = new byte[8192];
        char[] chars = new char[8192];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        StringBuilder line = new();
        string eventName = "message";
        List<string> data = new();
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            result.TFirstByteMs ??= now();
            result.Bytes += read;
            int count = decoder.GetChars(buffer, 0, read, chars, 0, false);
            for (int index = 0; index < count; index++)
            {
                char character = chars[index];
                if (character != '\n')
                {
                    line.Append(character);
                    continue;
                }

                string text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (ndjson)
                {
                    if (text.Length > 0)
                    {
                        DispatchNdjson(text);
                    }
                }
                else if (text.Length == 0)
                {
                    if (data.Count > 0)
                    {
                        Dispatch(eventName, string.Join('\n', data));
                        eventName = "message";
                        data.Clear();
                    }
                }
                else if (text.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = text[6..].Trim();
                }
                else if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Add(text[5..].TrimStart());
                }
                // 其余为 ": heartbeat" 之类的注释行，忽略。
            }
        }

        if (!ndjson && data.Count > 0)
        {
            Dispatch(eventName, string.Join('\n', data));
        }

        if (result.Outcome.Length == 0)
        {
            result.Outcome = state.Done ? state.Error ? "stream_error" : "ok" : "incomplete";
        }

        void DispatchNdjson(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            string type = document.RootElement.TryGetProperty("type", out JsonElement typeValue)
                && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString() ?? "message"
                : "message";
            Dispatch(type, json);
        }

        void Dispatch(string type, string payload)
        {
            result.Events++;
            double at = now();
            result.TFirstEventMs ??= at;
            switch (type)
            {
                case "conversation":
                    result.TConversationMs ??= at;
                    break;
                case "runtime":
                    result.TRuntimeMs ??= at;
                    break;
                case "content":
                    result.TFirstTokenMs ??= at;
                    result.ContentEvents++;
                    break;
                case "reasoning":
                    result.TFirstReasoningMs ??= at;
                    break;
                case "tool_call":
                    result.ToolCalls++;
                    break;
                case "error":
                    state.Error = true;
                    result.Detail = Cfg.Truncate(ErrorTitle(payload), 160);
                    break;
                case "done":
                    state.Done = true;
                    result.TDoneMs = at;
                    (result.DoneStatus, result.TraceId) = DoneStatus(payload, result.TraceId);
                    break;
                default:
                    break;
            }

            if (options.AbortAfterContent > 0 && result.ContentEvents >= options.AbortAfterContent && !state.AbortTriggered)
            {
                state.AbortTriggered = true;
                if (options.CancelApi)
                {
                    state.CancelTask = CancelAsync(result.ConversationId, CancellationToken.None);
                }
                else
                {
                    cts.Cancel();
                }
            }
        }
    }

    private async Task<int> CancelAsync(string conversationId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpRequestMessage request = new(HttpMethod.Post,
            options.BaseUrl + "/api/v1/agent/conversations/" + Uri.EscapeDataString(conversationId) + "/cancel");
        Authorize(request);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private async Task ReportProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (Volatile.Read(ref _finished) == 0)
            {
                await Task.Delay(5000, cancellationToken);
                Console.WriteLine(
                    $".. {_clock.Elapsed.TotalSeconds,5:F0}s completed={_completed} inflight={Volatile.Read(ref _inFlight)} outcomes="
                    + string.Join(' ', Sorted(_outcomes).Select(item => item.Key + ":" + item.Value))
                    + (Volatile.Read(ref _overload) > 0 ? " overload=" + Volatile.Read(ref _overload) : string.Empty));
            }
        }
        catch (OperationCanceledException)
        {
            // 停止时退出循环。
        }
    }

    /// <summary>从 ProblemDetails 里取出可读的错误标题；非 JSON 响应返回截断后的正文。</summary>
    private static string ProblemTitle(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            foreach (string key in new[] { "title", "detail" })
            {
                if (document.RootElement.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }
        catch (JsonException)
        {
            return Cfg.Truncate(body, 160);
        }
    }

    /// <summary>从 SSE error 事件或 NDJSON error 行里取出错误标题。</summary>
    private static string ErrorTitle(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                && nested.TryGetProperty("title", out JsonElement nestedTitle) && nestedTitle.ValueKind == JsonValueKind.String)
            {
                return nestedTitle.GetString() ?? string.Empty;
            }

            return root.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String
                ? title.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return "unparseable error payload";
        }
    }

    private static (string Status, string TraceId) DoneStatus(string payload, string currentTraceId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string status = root.TryGetProperty("status", out JsonElement statusValue) && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString() ?? string.Empty
                : string.Empty;
            string traceId = root.TryGetProperty("traceId", out JsonElement traceValue) && traceValue.ValueKind == JsonValueKind.String
                ? traceValue.GetString() ?? currentTraceId
                : currentTraceId;
            return (status, traceId);
        }
        catch (JsonException)
        {
            return (string.Empty, currentTraceId);
        }
    }

    private sealed class StreamState
    {
        public bool Done;

        public bool Error;

        public bool AbortTriggered;

        public Task<int>? CancelTask;
    }
}
