using OpenAgent.Core.Exten;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Engine.Extensions;
using OpenAgent.Engine.Host;
using OpenAgent.Engine.Host.Files;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Health;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Host.Skills;
using OpenAgent.Hosting;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// 文件上传直连 Engine 时（或经 Router 转发到 Engine 时），Kestrel 默认 30 MB 请求体上限
// 会在进入端点校验前拒绝更大的附件；与 FileAssets:MaxFileSizeBytes（默认 100 MB，
// 单请求单文件）对齐放开，并留出 multipart 开销余量。
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 256L * 1024 * 1024);

builder.Host.UseAgentSerilog("agent-engine");

builder.Services.AddAgentHost(builder.Configuration, options =>
{
    options.ServiceName = "openagent-engine";
    options.OpenTelemetrySource = "OpenAgent.Engine";
});

builder.Services.AddAgentCore(builder.Configuration);
builder.Services.AddOpenAgentInfrastructure(builder.Configuration);
builder.Services.AddFileAssetObjectStorage(builder.Configuration);
builder.Services.AddSingleton<SkillPackageManagementService>();

builder.Services.AddAgentEngine(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["infrastructure", "ready"]);
builder.Services.AddEngineErrorHandling();
var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    IDbContextFactory<OpenAgentDbContext> contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
    await using OpenAgentDbContext database = await contexts.CreateDbContextAsync();
    await database.Database.MigrateAsync();
}

app.UseAgentHost(builder.Configuration);
app.UseAgentErrorHandling();
app.UseMiddleware<AgentUserContextMiddleware>();
app.UseMiddleware<EngineAdmissionMiddleware>();
app.MapAgentAuthenticationEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapManagementEndpoints();
    app.MapConfigurationEndpoints();
}
app.MapAgentEndpoints();
app.MapFileShareDownloads();
app.MapHealthReport();

app.Run();
