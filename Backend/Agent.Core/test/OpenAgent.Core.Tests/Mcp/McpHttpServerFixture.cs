using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace OpenAgent.Core.Tests.Mcp;

internal sealed class McpHttpServerFixture : IAsyncDisposable
{
    private readonly WebApplication _application;

    private McpHttpServerFixture(WebApplication application, Uri endpoint)
    {
        _application = application;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public static async Task<McpHttpServerFixture> StartAsync(string route)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools([
                McpServerTool.Create(
                    (Func<string>)(() => "sdk-http-tool-result"),
                    new McpServerToolCreateOptions
                    {
                        Name = "lookup",
                        Description = "Lookup data"
                    })
            ]);

        WebApplication application = builder.Build();
        application.MapMcp(route);
        await application.StartAsync().ConfigureAwait(false);

        IServer server = application.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Server addresses are unavailable.");
        string baseAddress = addresses.Addresses.Single().TrimEnd('/');
        string endpoint = string.IsNullOrEmpty(route)
            ? $"{baseAddress}/"
            : $"{baseAddress}{route}";

        return new McpHttpServerFixture(application, new Uri(endpoint, UriKind.Absolute));
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
