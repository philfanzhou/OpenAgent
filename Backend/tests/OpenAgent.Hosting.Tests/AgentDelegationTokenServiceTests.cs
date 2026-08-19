using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenAgent.Hosting.Authentication;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class AgentDelegationTokenServiceTests
{
    [Fact]
    public void CreateToken_ContainsDelegatedIdentityAndValidates()
    {
        AgentDelegationTokenService service = CreateService();
        string token = service.CreateToken(new AgentDelegationIdentity(
            "user-1",
            "tenant-1",
            ["group-1"],
            ["operator"],
            new Dictionary<string, string> { ["department"] = "finance" },
            ["chat"]));

        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
        ClaimsPrincipal principal = handler.ValidateToken(
            token,
            service.CreateValidationParameters(),
            out _);

        Assert.Equal("user-1", principal.FindFirstValue(JwtRegisteredClaimNames.Sub));
        Assert.Equal("tenant-1", principal.FindFirstValue("tenant_id"));
        Assert.Equal("ProviderDelegation", principal.FindFirstValue("auth_mode"));
        Assert.Equal("agent.provider", principal.FindFirstValue("scope"));
        Assert.Equal("finance", principal.FindFirstValue("department"));
        Assert.Equal("operator", principal.FindFirstValue("roles"));
    }

    [Fact]
    public void CreateToken_TamperedToken_IsRejected()
    {
        AgentDelegationTokenService service = CreateService();
        string token = service.CreateToken(new AgentDelegationIdentity(
            "user-1",
            "tenant-1",
            [],
            [],
            new Dictionary<string, string>(),
            []));
        string tampered = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(
            tampered,
            service.CreateValidationParameters(),
            out _));
    }

    private static AgentDelegationTokenService CreateService() =>
        new(Options.Create(new AgentDelegationTokenOptions
        {
            SigningKey = "test-provider-token-signing-key-32-bytes"
        }));
}
