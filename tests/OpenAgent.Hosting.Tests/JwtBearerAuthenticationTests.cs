using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class JwtBearerAuthenticationTests
{
    private const string Issuer = "https://identity.example";
    private const string Audience = "openagent-api";
    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("openagent-test-signing-key-at-least-32-bytes"));

    [Fact]
    public async Task AuthenticateAsync_VerifiedToken_ReturnsClaimsIdentity()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Bearer {CreateToken(DateTime.UtcNow.AddMinutes(5))}";

        AuthenticateResult result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(result.Succeeded);
        Assert.Equal("user-1", result.Principal?.FindFirst("sub")?.Value);
        Assert.Equal("tenant-1", result.Principal?.FindFirst("tid")?.Value);
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiredToken_IsRejected()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Bearer {CreateToken(DateTime.UtcNow.AddMinutes(-1))}";

        AuthenticateResult result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        Assert.False(result.Succeeded);
        Assert.IsType<SecurityTokenExpiredException>(result.Failure);
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "JwtBearer",
                ["Authentication:Authority"] = Issuer,
                ["Authentication:Audience"] = Audience,
                ["Authentication:ClientId"] = "openagent-chat",
                ["Authentication:ClockSkewSeconds"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            options.Configuration.SigningKeys.Add(SigningKey);
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.ValidAudience = Audience;
            options.TokenValidationParameters.IssuerSigningKey = SigningKey;
        });
        return services.BuildServiceProvider();
    }

    private static string CreateToken(DateTime expires)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", "user-1"),
                new Claim("tid", "tenant-1")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: expires,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
