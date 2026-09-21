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
app.Run();

public partial class Program;
