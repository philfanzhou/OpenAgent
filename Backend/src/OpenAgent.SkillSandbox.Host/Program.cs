using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenAgent.Contracts.Skills;
using OpenAgent.SkillSandbox.Host;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
SandboxOptions options = builder.Configuration
    .GetSection(SandboxOptions.SectionName)
    .Get<SandboxOptions>() ?? new SandboxOptions();
Validate(options);
PrepareSocket(options.SocketPath);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenUnixSocket(
        options.SocketPath,
        listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ScriptExecutionService>();

WebApplication app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() => ProtectSocket(options.SocketPath));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/v1/execute", async (
    SkillScriptExecutionRequest request,
    ScriptExecutionService scripts,
    CancellationToken cancellationToken) =>
{
    try
    {
        SkillScriptExecutionResult result = await scripts.ExecuteAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }
    catch (SandboxBusyException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Skill sandbox is busy",
            detail: exception.Message);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
app.Run();

static void Validate(SandboxOptions options)
{
    if (string.IsNullOrWhiteSpace(options.SocketPath)
        || string.IsNullOrWhiteSpace(options.Interpreter))
    {
        throw new InvalidOperationException("Sandbox SocketPath and Interpreter are required.");
    }
    if (options.TimeoutSeconds <= 0
        || options.MaxScriptBytes <= 0
        || options.MaxOutputBytes <= 0
        || options.MaxArgumentCount <= 0
        || options.MaxArgumentLength <= 0
        || options.AllowedExtensions.Count == 0)
    {
        throw new InvalidOperationException("Sandbox limits must be positive and extensions cannot be empty.");
    }
}

static void PrepareSocket(string socketPath)
{
    string directory = Path.GetDirectoryName(socketPath)
        ?? throw new InvalidOperationException("Sandbox socket path requires a directory.");
    Directory.CreateDirectory(directory);
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

static void ProtectSocket(string socketPath)
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    File.SetUnixFileMode(
        socketPath,
        UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

public partial class Program;
