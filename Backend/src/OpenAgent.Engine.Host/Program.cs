using OpenAgent.Core.Exten;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Engine.Extensions;
using OpenAgent.Engine.Host.Files;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Health;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Hosting;
using OpenAgent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseAgentSerilog("agent-engine");

builder.Services.AddAgentHost(builder.Configuration, options =>
{
    options.ServiceName = "agent-engine";
    options.OpenTelemetrySource = "OpenAgent.Engine";
});

builder.Services.AddAgentCore(builder.Configuration);
builder.Services.AddOpenAgentInfrastructure(builder.Configuration);
builder.Services.AddFileAssetObjectStorage(builder.Configuration);

builder.Services.AddAgentEngine(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["infrastructure", "ready"]);
builder.Services.AddSingleton<ProblemDetailsFactory>();
builder.Services.AddSingleton<ErrorMapper>();
var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    IDbContextFactory<OpenAgentDbContext> contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
    await using OpenAgentDbContext database = await contexts.CreateDbContextAsync();
    await database.Database.MigrateAsync();
}

app.UseAgentHost(builder.Configuration);
app.UseMiddleware<AgentExceptionHandlerMiddleware>();
app.UseMiddleware<AgentUserContextMiddleware>();
app.UseMiddleware<EngineAdmissionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.MapAuthenticationEndpoints();
    app.MapManagementEndpoints();
}
app.MapAgentEndpoints();
app.MapHealthReport();

app.Run();
