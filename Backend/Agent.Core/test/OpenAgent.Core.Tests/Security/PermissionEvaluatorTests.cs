using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public sealed class PermissionEvaluatorTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task IsAuthenticatedAsync_UsesUserAuthenticationState(
        bool authenticated,
        bool expected)
    {
        var evaluator = new PermissionEvaluator(
            NullLogger<PermissionEvaluator>.Instance);
        var userContext = new AgentUserContext
        {
            UserId = "user-1",
            IsAuthenticated = authenticated
        };

        var result = await evaluator.IsAuthenticatedAsync(userContext);

        Assert.Equal(expected, result);
    }
}
