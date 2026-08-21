using System.Security.Claims;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.UserProfile;

internal sealed class UserProfileCapabilitySource : ICapabilitySource
{
    private const string Name = "get_current_user_profile";
    private const string Description =
        "Get the username and email of the current authenticated user. This function takes no arguments and cannot query another user.";
    private const string ParametersJsonSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> definitions = !user.IsAuthenticated
            ? []
            :
            [
                new CapabilityDefinition(
                    Name,
                    Description,
                    ParametersJsonSchema,
                    AgentResourceType.Function,
                    Name,
                    (_, _) => Task.FromResult(SerializeProfile(user)))
            ];
        return Task.FromResult(definitions);
    }

    private static string SerializeProfile(IAgentUserContext user)
    {
        UserProfile profile = new()
        {
            Username = ReadClaim(
                user.Claims,
                "preferred_username",
                "username",
                "name",
                ClaimTypes.Name),
            Email = ReadClaim(user.Claims, "email", ClaimTypes.Email)
        };
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    private static string? ReadClaim(
        IReadOnlyDictionary<string, string> claims,
        params string[] claimTypes)
    {
        foreach (string claimType in claimTypes)
        {
            if (claims.TryGetValue(claimType, out string? value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
