using OpenAgent.Core.Exten;
using OpenAgent.Engine.Extensions;
using OpenAgent.Engine.Host.Attachments;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseAgentSerilog("agent-engine");

builder.Services.AddAgentHost(builder.Configuration, options =>
{
    options.ServiceName = "agent-engine";
    options.OpenTelemetrySource = "OpenAgent.Engine";
});

builder.Services.AddAgentCore(builder.Configuration);

builder.Services.AddAgentEngine(builder.Configuration);
builder.Services.AddSingleton<ProblemDetailsFactory>();
builder.Services.AddSingleton<ErrorMapper>();
builder.Services.Configure<AgentAttachmentOptions>(
    builder.Configuration.GetSection(AgentAttachmentOptions.SectionName));
builder.Services.AddAttachmentStorage(builder.Configuration);
builder.Services.AddScoped<AgentAttachmentReader>();

var app = builder.Build();

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

app.Run();
