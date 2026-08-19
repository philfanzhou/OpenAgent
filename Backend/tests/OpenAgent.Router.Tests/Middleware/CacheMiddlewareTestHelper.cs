using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Tests.Middleware;

internal static class CacheMiddlewareTestHelper
{
    internal static AgentUserContext CreateUser(
        string userId = "user-1",
        string tenantId = "tenant-1") => new()
        {
            UserId = userId,
            TenantId = tenantId,
            IsAuthenticated = true
        };

    internal static DefaultHttpContext CreateContext(
        string body = "{\"message\":\"hello\"}",
        string path = "/api/v1/agent/chat")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    internal static RouterCacheSettings CreateSettings(
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["RouterSettings:Caching:MaxRequestBodyBytes"] = "1048576",
            ["RouterSettings:Caching:MaxResponseBodyBytes"] = "4194304",
            ["RouterSettings:Caching:IdempotencyTtlSeconds"] = "3600",
            ["RouterSettings:Caching:IdempotencyPendingTtlSeconds"] = "30",
            ["RouterSettings:Caching:QueryTtlSeconds"] = "300"
        };
        if (overrides != null)
        {
            foreach (KeyValuePair<string, string?> pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new RouterCacheSettings(configuration);
    }

    internal static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
