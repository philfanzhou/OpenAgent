using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using OpenAgent.Runner;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = ExecutionLimits.MaxWireBytes);
builder.Services.AddOptions<RunnerOptions>().Bind(builder.Configuration.GetSection("Runner"))
    .Validate(options => options.ApiKey.Length >= 32, "Runner:ApiKey must contain at least 32 characters.")
    .Validate(options => Path.IsPathFullyQualified(options.WorkspaceRoot)
        && !options.WorkspaceRoot.Contains(',') && options.WorkspaceRoot != "/"
        && !options.WorkspaceRoot.Contains('\n'), "Runner:WorkspaceRoot must be a dedicated absolute directory.")
    .Validate(options => Path.IsPathFullyQualified(options.BubblewrapPath)
        && Path.IsPathFullyQualified(options.PythonPath), "Runner executable paths must be absolute.")
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 600
        && options.MaxConcurrentExecutions is >= 1 and <= 16
        && options.MemoryMiB is >= 128 and <= 8192
        && options.WorkspaceMiB is >= 16 and <= 1024
        && options.MaxProcesses is >= 16 and <= 512, "Invalid Runner resource limits.")
    .ValidateOnStart();
builder.Services.AddSingleton<BubblewrapProcess>();
builder.Services.AddSingleton<ICodeExecutor, BubblewrapCodeExecutor>();
builder.Services.AddHostedService<WorkspaceReaper>();
WebApplication app = builder.Build();
app.MapGet("/health", async (BubblewrapProcess bubblewrap, CancellationToken cancellationToken) =>
{
    string sandboxFiles = Path.Combine(AppContext.BaseDirectory, "sandbox");
    return await bubblewrap.IsAvailableAsync(sandboxFiles, cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Problem("The Bubblewrap execution environment is unavailable.", statusCode: 503);
});
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
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next(context);
});
app.MapPost("/v1/execute", async (CodeExecutionRequest request, ICodeExecutor executor, HttpContext context) =>
{
    try
    {
        return Results.Ok(await executor.ExecuteAsync(request, context.RequestAborted));
    }
    catch (ArgumentException)
    {
        return Results.Problem("Invalid code or input files.", statusCode: 400);
    }
    catch (RunnerBusyException)
    {
        return Results.Problem("Runner concurrency limit reached.", statusCode: 429);
    }
    catch (Exception exception) when (exception is InvalidOperationException or JsonException
        or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
    {
        return Results.Problem("The isolated execution environment is unavailable or returned an invalid result.", statusCode: 503);
    }
});
app.Run();

public partial class Program;
