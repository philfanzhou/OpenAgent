using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class RouterProblem
{
    internal static IResult From(AgentRoutingException exception) => Results.Problem(
        statusCode: exception.StatusCode,
        title: exception.Title,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = exception.Code
        });
}
