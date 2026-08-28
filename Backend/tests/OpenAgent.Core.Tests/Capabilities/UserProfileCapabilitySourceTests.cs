using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.UserProfile;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class UserProfileCapabilitySourceTests
{
    [Fact]
    public async Task CreateAsync_AuthenticatedUser_ReturnsCurrentProfile()
    {
        AgentUserContext user = CreateUser(new Dictionary<string, string>
        {
            ["preferred_username"] = "alice",
            ["email"] = "alice@example.com"
        });
        CapabilityToolFactory factory = CreateFactory();

        AIFunction function = await CreateFunctionAsync(factory, user);
        object? result = await function.InvokeAsync(new AIFunctionArguments(), default);

        using JsonDocument profile = JsonDocument.Parse(Assert.IsType<string>(result));
        Assert.Equal("alice", profile.RootElement.GetProperty("username").GetString());
        Assert.Equal("alice@example.com", profile.RootElement.GetProperty("email").GetString());
        Assert.Equal("current-tenant", profile.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task CreateAsync_MissingProfileClaims_ReturnsNullFields()
    {
        CapabilityToolFactory factory = CreateFactory();

        AIFunction function = await CreateFunctionAsync(
            factory,
            CreateUser(new Dictionary<string, string>(), tenantId: null));
        object? result = await function.InvokeAsync(new AIFunctionArguments(), default);

        using JsonDocument profile = JsonDocument.Parse(Assert.IsType<string>(result));
        Assert.Equal(JsonValueKind.Null, profile.RootElement.GetProperty("username").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.RootElement.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.RootElement.GetProperty("tenantId").ValueKind);
    }

    [Fact]
    public async Task CreateAsync_UnauthenticatedUser_ExcludesFunction()
    {
        CapabilityToolFactory factory = CreateFactory();
        AgentUserContext user = CreateUser(
            new Dictionary<string, string>(),
            isAuthenticated: false);

        IReadOnlyList<AITool> tools = await factory.CreateAsync(
            "agent-1",
            new AgentConfig(),
            user,
            default);

        Assert.Empty(tools);
    }

    [Theory]
    [InlineData(AgentResourceType.Tool)]
    [InlineData(AgentResourceType.Function)]
    public async Task CreateAsync_UnauthorizedCapability_ExcludesFunction(
        AgentResourceType deniedResourceType)
    {
        CapabilityToolFactory factory = CreateFactory(
            new SelectiveAuthorizationService(deniedResourceType));

        IReadOnlyList<AITool> tools = await factory.CreateAsync(
            "agent-1",
            new AgentConfig(),
            CreateUser(new Dictionary<string, string>()),
            default);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task InvokeAsync_UserIdArgument_CannotReadAnotherUser()
    {
        AgentUserContext user = CreateUser(new Dictionary<string, string>
        {
            ["preferred_username"] = "current-user",
            ["email"] = "current@example.com"
        });
        AIFunction function = await CreateFunctionAsync(CreateFactory(), user);
        var arguments = new AIFunctionArguments
        {
            ["userId"] = "other-user"
        };

        object? result = await function.InvokeAsync(arguments, default);

        using JsonDocument profile = JsonDocument.Parse(Assert.IsType<string>(result));
        Assert.Equal("current-user", profile.RootElement.GetProperty("username").GetString());
        Assert.Equal("current@example.com", profile.RootElement.GetProperty("email").GetString());
        Assert.False(function.JsonSchema.GetProperty("properties").TryGetProperty("userId", out _));
    }

    [Fact]
    public async Task InvokeAsync_SensitiveClaims_DoesNotLeakSensitiveFields()
    {
        AgentUserContext user = CreateUser(new Dictionary<string, string>
        {
            [ClaimTypes.Name] = "safe-user",
            [ClaimTypes.Email] = "safe@example.com",
            ["password"] = "password-secret",
            ["access_token"] = "token-secret",
            ["api_key"] = "key-secret",
            ["raw_ticket"] = "ticket-secret"
        });
        AIFunction function = await CreateFunctionAsync(CreateFactory(), user);

        object? result = await function.InvokeAsync(new AIFunctionArguments(), default);
        string json = Assert.IsType<string>(result);

        using JsonDocument profile = JsonDocument.Parse(json);
        string[] propertyNames = profile.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(new[] { "username", "email", "tenantId" }, propertyNames);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    private static CapabilityToolFactory CreateFactory(
        IAgentAuthorizationService? authorization = null)
    {
        AgentAuthorizationGate gate = new(
            authorization ?? new AllowAllAgentAuthorizationService(),
            new Core.Models.LlmRegistry());
        return new CapabilityToolFactory([new UserProfileCapabilitySource()], gate);
    }

    private static async Task<AIFunction> CreateFunctionAsync(
        CapabilityToolFactory factory,
        AgentUserContext user)
    {
        IReadOnlyList<AITool> tools = await factory.CreateAsync(
            "agent-1",
            new AgentConfig(),
            user,
            default);
        return Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools));
    }

    private static AgentUserContext CreateUser(
        IReadOnlyDictionary<string, string> claims,
        bool isAuthenticated = true,
        string? tenantId = "current-tenant") => new()
        {
            UserId = "current-user-id",
            TenantId = tenantId,
            Claims = claims,
            IsAuthenticated = isAuthenticated
        };

    private sealed class SelectiveAuthorizationService(
        AgentResourceType deniedResourceType) : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.ResourceType != deniedResourceType);
    }
}
