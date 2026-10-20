using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Application.Exten;
using OpenAgent.Application.Runtime.Agent;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.OpenCodeDevHost;
using OpenAgent.Runtime;

// OpenCode ACP 执行路径（PR #234 的 OpenAgent.Runtime 实现）的本地开发宿主：
// 真实 Kestrel + 生产 Application 装配 + 每轮真实 opencode 进程，存储换为进程内
// 实现 —— 无 Postgres/Redis 也能发起会话。仅本机联调，不是生产 Engine.Host 的替代。

string url = args.Length > 0 ? args[0] : "http://127.0.0.1:5800";
string workspaceRoot = Path.Combine(Path.GetTempPath(), "openagent-acp-devhost");
// Windows 上 npm 安装的 opencode 只有 .cmd shim（CreateProcess 不能直接启动，且经
// cmd 转发在剥离环境下依赖 PATHEXT）；优先解析到包内原生 opencode.exe 直接启动。
string executable = Environment.GetEnvironmentVariable("DEVHOST_OPENCODE") ?? ResolveOpenCodeExecutable();
// 诊断口：DEVHOST_OPENCODE_ARGS 用逗号分隔覆盖默认参数（如包装脚本场景）。
string[] arguments = Environment.GetEnvironmentVariable("DEVHOST_OPENCODE_ARGS") is { } args
    ? args.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : ["acp"];
Console.WriteLine($"devhost opencode: {executable} {string.Join(' ', arguments)}");

// 模型凭证取自本机 opencode 用户配置（~/.config/opencode/opencode.jsonc），
// 启动时读取一次；密钥只进入 LlmConfig 与子进程环境，不写入日志。
(string endpoint, string model, string apiKey) = ReadLocalOpenCodeModel();
Console.WriteLine($"devhost model: {model} @ {endpoint} (key loaded, not printed)");

// 配置键必须与实际参数数量一致：多写的 Arguments:N 会绑成 null 并在
// ProcessStartInfo.ArgumentList 处抛 ArgumentNullException。
var openCodeSettings = new Dictionary<string, string?>
{
    ["OpenCode:Executable"] = executable,
    ["OpenCode:WorkspaceRoot"] = workspaceRoot,
    ["OpenCode:TurnTimeoutSeconds"] = "600",
    ["OpenCode:MaxConcurrentTurns"] = "4"
};
for (int index = 0; index < arguments.Length; index++)
{
    openCodeSettings[$"OpenCode:Arguments:{index}"] = arguments[index];
}
IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(openCodeSettings).Build();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = "Production",
    ApplicationName = "OpenAgent.OpenCodeDevHost"
});
builder.WebHost.UseKestrel();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.AddSingleton<IConfiguration>(configuration);

builder.Services.AddSingleton<ICurrentUserContext>(new DevUserContext());
builder.Services.AddSingleton<DevConversationStore>();
builder.Services.AddAgentApplication(configuration);
// ACP 路径的运行时解析换为固定 GLM 档案；模型客户端工厂保留真实注册、
// 用空交互日志喂饱其依赖（Runtime 路径不会触发它做推理）。
builder.Services.RemoveAll<IAgentRuntimeResolver>();
builder.Services.AddSingleton<IAgentRuntimeResolver>(new FixedRuntimeResolver(
    new LlmConfig
    {
        ModelId = model,
        Endpoint = endpoint,
        ApiKey = apiKey,
        ContextTokens = 128000
    }));
builder.Services.RemoveAll<ILlmInteractionStore>();
builder.Services.AddSingleton<ILlmInteractionStore, NullLlmInteractionStore>();
// 进程内会话存储（重启即失，仅本机联调）。
builder.Services.RemoveAll<IConversationStore>();
builder.Services.AddSingleton<IConversationStore>(provider =>
    new DevConversationStore.Adapter(provider.GetRequiredService<DevConversationStore>()));
builder.Services.RemoveAll<IFileAssetRepository>();
builder.Services.AddSingleton<IFileAssetRepository, NullFileAssetRepository>();
// OpenCode ACP 运行时（与生产 Engine.Host 相同的注册入口）。
builder.Services.AddOpenCodeRuntime(configuration);

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

WebApplication app = builder.Build();
app.Urls.Clear();
app.Urls.Add(url);
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok", runtime = "opencode-acp" }));
app.MapGet("/", () => Results.Content(ChatPage.Html, "text/html; charset=utf-8"));

app.MapPost("/api/v1/agent/chat", async (ChatRequest body, AgentExecutor executor, HttpContext context, CancellationToken ct) =>
{
    AgentRequest request = CreateAgentRequest(body, context);
    AgentResponse response = await executor.ExecuteAsync(request, DevUserContext.User, ct);
    return Results.Ok(new ChatResponse
    {
        Message = response.Content,
        Usage = response.TokenUsage,
        ModelId = response.ModelId
    });
});

app.MapPost("/api/v1/agent/chat/stream",
    async (ChatRequest body, AgentExecutor executor, HttpContext context, CancellationToken ct) =>
{
    AgentRequest request = CreateAgentRequest(body, context);
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    await WriteSseAsync(context, "conversation",
        JsonSerializer.Serialize(new { conversationId = request.ConversationId }), ct);
    await foreach (AgentStreamEvent item in executor.ExecuteStreamingAsync(request, DevUserContext.User, ct))
    {
        if (item.Type == AgentStreamEventType.Usage)
        {
            continue;
        }
        string eventName = item.Type switch
        {
            AgentStreamEventType.Reasoning => "reasoning",
            AgentStreamEventType.ToolCall => "tool_call",
            AgentStreamEventType.ToolResult => "tool_result",
            AgentStreamEventType.PlanUpdated => "plan_updated",
            _ => "content"
        };
        await WriteSseAsync(context, eventName, JsonSerializer.Serialize(new
        {
            content = item.Content,
            toolName = item.ToolName,
            toolCallId = item.ToolCallId,
            toolArguments = item.ToolArguments
        }), ct);
    }
    await WriteSseAsync(context, "done", JsonSerializer.Serialize(new
    {
        done = true,
        conversationId = request.ConversationId,
        traceId = request.TraceId
    }), ct);
});

app.Run();

static AgentRequest CreateAgentRequest(ChatRequest body, HttpContext context)
{
    // 与生产 AgentEndpointRequestMapper 同序：Context 字典 → 请求头 → 生成新会话 ID。
    string? ReadContext(string key) =>
        body.Context != null && body.Context.TryGetValue(key, out object? value)
            ? value?.ToString()
            : null;
    return new AgentRequest
    {
        Query = body.Message,
        AgentId = ReadContext("agentId") ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
        // RuntimeAgentExecutor 要求非空；devhost 的 FixedRuntimeResolver 忽略该值，
        // 固定返回启动时从本机 opencode 配置读取的 GLM 档案。
        LlmProfileId = ReadContext("llmProfileId")
            ?? context.Request.Headers["X-OpenAgent-Llm-Profile-Id"].FirstOrDefault()
            ?? "devhost-local-glm",
        ConversationId = ReadContext("conversationId")
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString(),
        TraceId = context.Request.Headers["X-Trace-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N"),
        FileIds = body.FileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray()
    };
}

static async Task WriteSseAsync(HttpContext context, string eventName, string data, CancellationToken ct)
{
    await context.Response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
}

/// <summary>沿 PATH 解析 opencode：Windows 优先返回 npm 包内原生 exe，
/// 避免经 .cmd shim（CreateProcess 限制 + PATHEXT 依赖）；其他平台原样返回。</summary>
static string ResolveOpenCodeExecutable()
{
    if (!OperatingSystem.IsWindows())
    {
        return "opencode";
    }
    string? pathVariable = Environment.GetEnvironmentVariable("PATH");
    foreach (string directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        try
        {
            string native = Path.Combine(directory, "node_modules", "opencode-ai", "bin", "opencode.exe");
            if (File.Exists(native))
            {
                return native;
            }
            string exe = Path.Combine(directory, "opencode.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }
        catch (ArgumentException)
        {
            // PATH 中含非法字符的目录直接跳过。
        }
    }
    return "opencode";
}

static (string endpoint, string model, string apiKey) ReadLocalOpenCodeModel()
{    string configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "opencode", "opencode.jsonc");
    if (!File.Exists(configPath))
    {
        throw new InvalidOperationException(
            $"未找到本机 opencode 模型配置 {configPath}；devhost 依赖其中的 GLM provider 配置。");
    }
    string raw = File.ReadAllText(configPath);
    string apiKey = Regex.Match(raw, "\"apiKey\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
    string endpoint = Regex.Match(raw, "\"baseURL\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
    string model = Regex.Match(raw, "\"model\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
    if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model))
    {
        throw new InvalidOperationException("opencode.jsonc 缺少 baseURL/apiKey/model，无法构造模型档案。");
    }
    // "glm/glm-5.3-flash" → 取 provider 之后的模型名。
    int slash = model.IndexOf('/');
    if (slash >= 0)
    {
        model = model[(slash + 1)..];
    }
    return (endpoint, model, apiKey);
}

internal sealed class DevUserContext : ICurrentUserContext
{
    internal static readonly AgentUserContext User = new()
    {
        UserId = "local-user",
        TenantId = "local-dev",
        IsAuthenticated = true
    };

    public string UserId => User.UserId;
    public string? Username => null;
    public string? Email => null;
    public string? TenantId => User.TenantId;
    public IReadOnlyList<string> Groups => [];
    public IReadOnlyList<string> Roles => [];
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Audience => [];
    public bool IsAuthenticated => true;
    public bool IsInRole(string role) => false;
}

internal sealed class FixedRuntimeResolver(LlmConfig model) : IAgentRuntimeResolver
{
    public Task<AgentRuntimeProfile> ResolveAsync(
        string agentId,
        string llmProfileId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentRuntimeProfile
        {
            AgentId = agentId,
            Config = new AgentConfig { MaxTurns = 4 },
            Model = model
        });
}

internal sealed class NullLlmInteractionStore : ILlmInteractionStore
{
    public Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
        string tenantId, string conversationId, int skip, int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LlmInteractionRecord>>([]);
}

internal sealed class NullFileAssetRepository : IFileAssetRepository
{
    public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
        Task.FromResult<FileAsset?>(null);
    public Task<IReadOnlyList<FileAsset>> ListReferencedAsync(string conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FileAsset>>([]);
    public Task EnsureConversationReferencesAsync(string conversationId, IReadOnlyList<string> fileIds, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
        Task.CompletedTask;
    public Task<bool> IsReferencedAsync(string conversationId, string fileId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>极简联调页面：POST 流式读取 SSE 并按事件类型渲染。</summary>
internal static class ChatPage
{
    internal const string Html = """
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<title>OpenAgent OpenCode Dev Host</title>
<style>
 body { font-family: system-ui, sans-serif; max-width: 860px; margin: 24px auto; padding: 0 12px; background: #fafafa; }
 #log { border: 1px solid #ddd; border-radius: 8px; background: #fff; height: 60vh; overflow-y: auto; padding: 12px; }
 .msg { margin: 6px 0; white-space: pre-wrap; }
 .user { color: #06c; font-weight: 600; }
 .reasoning { color: #888; font-style: italic; }
 .tool { color: #c60; font-family: ui-monospace, monospace; font-size: 13px; }
 .done { color: #080; font-size: 13px; border-top: 1px dashed #ccc; margin-top: 8px; padding-top: 4px; }
 #bar { display: flex; gap: 8px; margin-top: 12px; }
 #text { flex: 1; padding: 8px; font: inherit; }
 button { padding: 8px 18px; }
 #cid { width: 260px; font-family: ui-monospace, monospace; font-size: 12px; }
</style>
</head>
<body>
<h3>OpenAgent · OpenCode ACP 执行路径（PR #234 实现）</h3>
<div>会话 ID：<input id="cid" placeholder="留空则新建会话"> <span id="status"></span></div>
<div id="log"></div>
<div id="bar">
  <textarea id="text" rows="2" placeholder="输入消息，Ctrl+Enter 发送"></textarea>
  <button id="send">发送</button>
</div>
<script>
const log = document.getElementById('log'), cid = document.getElementById('cid'),
      text = document.getElementById('text'), statusEl = document.getElementById('status');
function add(cls, s) {
  const d = document.createElement('div'); d.className = 'msg ' + cls; d.textContent = s;
  log.appendChild(d); log.scrollTop = log.scrollHeight;
}
document.getElementById('send').onclick = send;
text.addEventListener('keydown', e => { if (e.ctrlKey && e.key === 'Enter') send(); });
async function send() {
  const message = text.value.trim(); if (!message) return;
  text.value = ''; add('user', message); statusEl.textContent = ' · 生成中…';
  try {
    const res = await fetch('/api/v1/agent/chat/stream', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Conversation-Id': cid.value || '' },
      body: JSON.stringify({ message })
    });
    const reader = res.body.getReader(), decoder = new TextDecoder();
    let buf = '', reasoning = '', content = '';
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      buf += decoder.decode(value, { stream: true });
      let idx;
      while ((idx = buf.indexOf('\n\n')) >= 0) {
        const frame = buf.slice(0, idx); buf = buf.slice(idx + 2);
        const ev = (frame.match(/^event: (.+)$/m) || [])[1];
        const data = (frame.match(/^data: (.+)$/m) || [])[1];
        if (!ev || !data) continue;
        const j = JSON.parse(data);
        if (ev === 'conversation' && j.conversationId) { cid.value = j.conversationId; }
        else if (ev === 'reasoning') reasoning += j.content || '';
        else if (ev === 'content') content += j.content || '';
        else if (ev === 'tool_call') add('tool', '🔧 ' + (j.toolName || j.toolCallId) + (j.toolArguments ? ' ' + JSON.stringify(j.toolArguments) : ''));
        else if (ev === 'tool_result') add('tool', '↳ ' + ((j.content || '').slice(0, 300)));
        else if (ev === 'plan_updated') add('tool', '📋 ' + (j.content || '').slice(0, 300));
        else if (ev === 'done') add('done', '—— 完成 · 会话 ' + (j.conversationId || '').slice(0, 12) + ' ——');
      }
    }
    if (reasoning) add('reasoning', '思考：' + reasoning.slice(0, 2000));
    if (content) add('', content);
  } catch (e) { add('reasoning', '连接中断（已取消）：' + e.message); }
  statusEl.textContent = '';
}
</script>
</body>
</html>
""";
}
