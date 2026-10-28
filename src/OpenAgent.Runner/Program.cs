using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using OpenAgent.Runner;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// /health 被编排器高频轮询；Runner 未接入 OTel，压掉 ASP.NET Core 默认的请求生命周期
// Information 日志，避免健康探测淹没 stdout 中的业务日志（异常仍以 Warning+ 输出）。
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = ExecutionLimits.MaxWireBytes);
// Swagger 仅在开发环境暴露；Runner 刻意不引用 OpenAgent.Hosting，保持沙箱 sidecar 的轻依赖。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}
builder.Services.AddOptions<RunnerOptions>().Bind(builder.Configuration.GetSection("Runner"))
    .Validate(options => options.ApiKey.Length >= 32, "Runner:ApiKey must contain at least 32 characters.")
    .Validate(options => Path.IsPathFullyQualified(options.WorkspaceRoot)
        && !options.WorkspaceRoot.Contains(',') && options.WorkspaceRoot != "/"
        && !options.WorkspaceRoot.Contains('\n'), "Runner:WorkspaceRoot must be a dedicated absolute directory.")
    .Validate(options => Path.IsPathFullyQualified(options.BubblewrapPath)
        && Path.IsPathFullyQualified(options.PythonPath)
        && Path.IsPathFullyQualified(options.NodePath), "Runner executable paths must be absolute.")
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 600
        && options.MaxConcurrentExecutions is >= 1 and <= 16
        && options.MemoryMiB is >= 128 and <= 8192
        && options.WorkspaceMiB is >= 16 and <= 1024
        && options.MaxProcesses is >= 16 and <= 512
        && options.MaxSessionSandboxes is >= 1 and <= 256
        && options.SessionIdleMinutes is >= 1, "Invalid Runner resource limits.")
    .ValidateOnStart();
builder.Services.AddSingleton<BubblewrapProcess>();
builder.Services.AddSingleton<SessionSandboxManager>();
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<ICodeExecutor, BubblewrapCodeExecutor>();
builder.Services.AddHostedService<WorkspaceReaper>();
WebApplication app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapGet("/health", async (BubblewrapProcess bubblewrap, CancellationToken cancellationToken) =>
{
    string sandboxFiles = Path.Combine(AppContext.BaseDirectory, "sandbox");
    return await bubblewrap.IsAvailableAsync(sandboxFiles, cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(
            title: "ExecutionEnvironmentUnavailable",
            detail: "The Bubblewrap execution environment is unavailable.",
            statusCode: 503);
})
    .WithName("RunnerHealth")
    .WithTags("Runner");
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next(context);
        return;
    }
    string expected = context.RequestServices.GetRequiredService<IOptions<RunnerOptions>>().Value.ApiKey;
    string supplied = context.Request.Headers.Authorization.ToString();
    byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected));
    byte[] suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    if (!CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
    {
        // 与全平台一致的 ProblemDetails 错误契约（含 traceId/timestamp/code）。
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(RunnerProblem.Serialize(RunnerProblem.Create(
            "https://error.agent.com/authentication-required",
            "AuthenticationRequired",
            StatusCodes.Status401Unauthorized,
            "A valid Runner API key is required.",
            context)));
        return;
    }
    await next(context);
});
app.MapPost("/api/v1/execute",
    async Task<Results<Ok<CodeExecutionResult>, ProblemHttpResult>> (CodeExecutionRequest request, ICodeExecutor executor, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await executor.ExecuteAsync(request, context.RequestAborted));
    }
    catch (ArgumentException)
    {
        return TypedResults.Problem(RunnerProblem.Create(
            "https://error.agent.com/invalid-request",
            "InvalidRequest",
            StatusCodes.Status400BadRequest,
            "Invalid code or input files.",
            context));
    }
    catch (RunnerBusyException)
    {
        return TypedResults.Problem(RunnerProblem.Create(
            "https://error.agent.com/rate-limited",
            "RateLimited",
            StatusCodes.Status429TooManyRequests,
            "Runner concurrency limit reached.",
            context));
    }
    catch (Exception exception) when (exception is InvalidOperationException or JsonException
        or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
    {
        return TypedResults.Problem(RunnerProblem.Create(
            "https://error.agent.com/dependency-unavailable",
            "DependencyUnavailable",
            StatusCodes.Status503ServiceUnavailable,
            "The isolated execution environment is unavailable or returned an invalid result.",
            context));
    }
})
    .WithName("ExecuteCode")
    .WithTags("Runner");

// 会话工作区文件操作：直接作用于宿主侧 session-<key>/work（bind-mount 进沙箱 /work），
// 不需要沙箱进程存活；错误经 ProblemDetails 携带 traceId 回传 Engine。
app.MapPost("/api/v1/workspace/{sessionKey}/list",
    async Task<Results<Ok<WorkspaceListResult>, ProblemHttpResult>> (
        string sessionKey, WorkspaceListRequest request, WorkspaceStore store, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await store.WithSessionAsync(
            sessionKey,
            work => store.ListAsync(work, request.Path, request.Pattern),
            context.RequestAborted));
    }
    catch (Exception exception)
    {
        return WorkspaceProblem(exception, context);
    }
})
    .WithName("WorkspaceList")
    .WithTags("Runner");

app.MapPost("/api/v1/workspace/{sessionKey}/read",
    async Task<Results<Ok<WorkspaceReadResult>, ProblemHttpResult>> (
        string sessionKey, WorkspaceReadRequest request, WorkspaceStore store, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await store.WithSessionAsync(
            sessionKey,
            work => store.ReadAsync(work, request.Path, request.OffsetLine, request.LimitLines),
            context.RequestAborted));
    }
    catch (Exception exception)
    {
        return WorkspaceProblem(exception, context);
    }
})
    .WithName("WorkspaceRead")
    .WithTags("Runner");

app.MapPost("/api/v1/workspace/{sessionKey}/write",
    async Task<Results<Ok<WorkspaceWriteResult>, ProblemHttpResult>> (
        string sessionKey, WorkspaceWriteRequest request, WorkspaceStore store, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await store.WithSessionAsync(
            sessionKey,
            work => store.WriteAsync(work, request.Path, request.Content),
            context.RequestAborted));
    }
    catch (Exception exception)
    {
        return WorkspaceProblem(exception, context);
    }
})
    .WithName("WorkspaceWrite")
    .WithTags("Runner");

app.MapPost("/api/v1/workspace/{sessionKey}/edit",
    async Task<Results<Ok<WorkspaceEditResult>, ProblemHttpResult>> (
        string sessionKey, WorkspaceEditRequest request, WorkspaceStore store, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await store.WithSessionAsync(
            sessionKey,
            work => store.EditAsync(work, request.Path, request.OldString, request.NewString, request.ReplaceAll),
            context.RequestAborted));
    }
    catch (Exception exception)
    {
        return WorkspaceProblem(exception, context);
    }
})
    .WithName("WorkspaceEdit")
    .WithTags("Runner");

app.MapPost("/api/v1/workspace/{sessionKey}/bytes/read",
    async Task<Results<Ok<WorkspaceBytesResult>, ProblemHttpResult>> (
        string sessionKey, WorkspaceBytesRequest request, WorkspaceStore store, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await store.WithSessionAsync(
            sessionKey,
            work => store.ReadBytesAsync(work, request.Path),
            context.RequestAborted));
    }
    catch (Exception exception)
    {
        return WorkspaceProblem(exception, context);
    }
})
    .WithName("WorkspaceReadBytes")
    .WithTags("Runner");

app.MapPost("/api/v1/workspace/{sessionKey}/bytes/upload",
    async Task<Results<Ok<WorkspaceWriteResult>, ProblemHttpResult>> (
        string sessionKey, WorkspaceUploadRequest request, WorkspaceStore store, HttpContext context) =>
{
    try
    {
        return TypedResults.Ok(await store.WithSessionAsync(
            sessionKey,
            work => store.UploadAsync(work, request.Path, request.ContentBase64),
            context.RequestAborted));
    }
    catch (Exception exception)
    {
        return WorkspaceProblem(exception, context);
    }
})
    .WithName("WorkspaceUploadBytes")
    .WithTags("Runner");

app.Run();

static ProblemHttpResult WorkspaceProblem(Exception exception, HttpContext context)
{
    // 意外异常不回传原始消息（可能含宿主路径细节），只给领域错误携带 detail。
    (int status, string detail) = exception switch
    {
        WorkspacePathException error => (StatusCodes.Status400BadRequest, error.Message),
        WorkspaceFileNotFoundException error => (StatusCodes.Status404NotFound, error.Message),
        WorkspaceEditConflictException error => (StatusCodes.Status409Conflict, error.Message),
        WorkspaceTooLargeException error => (StatusCodes.Status413RequestEntityTooLarge, error.Message),
        _ => (StatusCodes.Status500InternalServerError, "The workspace operation failed unexpectedly.")
    };
    return TypedResults.Problem(RunnerProblem.Create(
        "https://error.agent.com/workspace-error",
        "WorkspaceError",
        status,
        detail,
        context));
}

public partial class Program;
