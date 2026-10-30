using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Capabilities.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Admin;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Framework;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ResourcePolicyIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PolicyEndpoints_EnforceAdministrationTenantScopeAndRevision()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        Configure(builder.Services, "Host=unused;Database=unused", null);
        builder.Services.RemoveAll<IDbContextOptionsConfiguration<OpenAgentDbContext>>();
        builder.Services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Features.Set(new AgentRequestFeature("policy-test", new AgentUserContext
            {
                UserId = "alice", TenantId = context.Request.Headers["X-Tenant"].FirstOrDefault() ?? "a",
                Roles = context.Request.Headers.ContainsKey("X-Admin") ? ["Admin"] : []
            }));
            return next(context);
        });
        app.MapGroup("/policies").MapResourcePolicyEndpoints();
        await app.StartAsync();
        using HttpClient client = new() { BaseAddress = new(app.Urls.Single()) };
        string url = "/policies/resource-policies/Mcp/server";
        ResourcePolicy policy = new() { Rules = [new() { Actions = ["use"], UserIds = ["alice"] }] };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(url, policy)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Admin", "true");
        using HttpResponseMessage created = await client.PutAsJsonAsync(url, policy);
        created.EnsureSuccessStatusCode();
        Assert.Equal(1, (await created.Content.ReadFromJsonAsync<ResourcePolicy>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(url, policy)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Tenant", "b");
        Assert.Equal(0, (await client.GetFromJsonAsync<ResourcePolicy>(url))!.Revision);
        client.DefaultRequestHeaders.Remove("X-Tenant");
        Assert.Equal(1, (await client.GetFromJsonAsync<ResourcePolicy>(url))!.Revision);
        using HttpResponseMessage revoked = await client.PutAsJsonAsync(url, new ResourcePolicy { Revision = 1 });
        revoked.EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<ResourcePolicy>(url))!.Rules);
    }

    [ResourceDatabaseFact]
    public async Task PostgreSqlAndRedis_TwoInstances_RevokeAndRotateWithoutWaitingForCacheTtl()
    {
        string connection = Environment.GetEnvironmentVariable("OPENAGENT_TEST_POLICY_POSTGRES")!;
        string redisAddress = Environment.GetEnvironmentVariable("OPENAGENT_TEST_POLICY_REDIS")!;
        await using ServiceProvider first = Build(connection, redisAddress);
        await using ServiceProvider second = Build(connection, redisAddress);
        await using (var db = await first.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>().CreateDbContextAsync())
            await db.Database.MigrateAsync();
        foreach (IHostedService service in first.GetServices<IHostedService>()) await service.StartAsync(default);
        foreach (IHostedService service in second.GetServices<IHostedService>()) await service.StartAsync(default);
        string tenant = "n06-" + Guid.NewGuid().ToString("N");
        IMcpConfigRepository writer = first.GetRequiredService<IMcpConfigRepository>();
        IMcpConfigRepository reader = second.GetRequiredService<IMcpConfigRepository>();
        // Warm a negative cache before create, and a positive cache before update/delete.
        Assert.Null(await reader.GetAsync(tenant, "server"));
        await writer.SaveAsync(tenant, new() { Name = "server", Url = "https://first.test/mcp" });
        await WaitForAsync(async () => await reader.GetAsync(tenant, "server") is not null);
        IResourcePolicyStore policies = first.GetRequiredService<IResourcePolicyStore>();
        var authorization = new ResourcePolicyAuthorizationService(second.GetRequiredService<IResourcePolicyStore>(),
            Options.Create(new AgentAuthorizationOptions { RequireResourcePolicy = true }));
        AgentUserContext user = new() { UserId = "alice", TenantId = tenant, Claims = new Dictionary<string, string> { ["scope"] = "mcp.use" } };
        AgentAuthorizationRequest request = new("agent", AgentResourceType.Mcp, "server", "use");
        ResourcePolicy allow = new() { Rules = [new() { Actions = ["use"], UserIds = ["alice"] }] };
        Assert.NotNull(await policies.SaveAsync(tenant, AgentResourceType.Mcp, "server", allow, 0));
        Assert.True(await authorization.IsAuthorizedAsync(request, user));
        Assert.False(await authorization.IsAuthorizedAsync(request, new AgentUserContext { UserId = "alice", TenantId = "foreign", Roles = ["Admin"] }));
        await using (AuthorizationLease lease = new(authorization.RevalidateAsync, default, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)))
        {
            Stopwatch clock = Stopwatch.StartNew();
            Assert.NotNull(await policies.SaveAsync(tenant, AgentResourceType.Mcp, "server", new(), 1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(4), lease.Token));
            output.WriteLine($"Cross-instance revocation signalled in {clock.ElapsedMilliseconds} ms (budget 3000 ms).");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
            Assert.False(await authorization.IsAuthorizedAsync(request, user));
        }
        // Compare-and-swap permits exactly one concurrent policy editor.
        Task<ResourcePolicy?>[] edits = [policies.SaveAsync(tenant, AgentResourceType.Mcp, "server", allow, 2),
            second.GetRequiredService<IResourcePolicyStore>().SaveAsync(tenant, AgentResourceType.Mcp, "server", allow, 2)];
        Assert.Single((await Task.WhenAll(edits)).Where(value => value is not null));
        var nextAuthorization = new ResourcePolicyAuthorizationService(second.GetRequiredService<IResourcePolicyStore>(), Options.Create(new AgentAuthorizationOptions()));
        Assert.True(await nextAuthorization.IsAuthorizedAsync(request, user));
        await using (AuthorizationLease lease = new(nextAuthorization.RevalidateAsync, default, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)))
        {
            await writer.SaveAsync(tenant, new() { Name = "server", Url = "https://rotated.test/mcp" });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(4), lease.Token));
            await WaitForAsync(async () => (await reader.GetAsync(tenant, "server"))?.Url == "https://rotated.test/mcp");
        }
        Assert.True(await writer.DeleteAsync(tenant, "server"));
        await WaitForAsync(async () => await reader.GetAsync(tenant, "server") is null);
        Assert.False(await nextAuthorization.IsAuthorizedAsync(request, user));
        foreach (IHostedService service in second.GetServices<IHostedService>()) await service.StopAsync(default);
        foreach (IHostedService service in first.GetServices<IHostedService>()) await service.StopAsync(default);
    }

    private static ServiceProvider Build(string postgres, string redis)
    {
        ServiceCollection services = new();
        services.AddLogging();
        Configure(services, postgres, redis);
        return services.BuildServiceProvider();
    }

    private static void Configure(IServiceCollection services, string postgres, string? redis)
    {
        if (redis is not null) services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        services.AddOpenAgentFramework(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OpenAgentDatabase"] = postgres,
            ["ConversationCache:Enabled"] = "false"
        }).Build());
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
}

internal sealed class ResourceDatabaseFactAttribute : FactAttribute
{
    public ResourceDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAGENT_TEST_POLICY_POSTGRES"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAGENT_TEST_POLICY_REDIS")))
            Skip = "Set OPENAGENT_TEST_POLICY_POSTGRES and OPENAGENT_TEST_POLICY_REDIS to disposable test services.";
    }
}
