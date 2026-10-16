namespace OpenAgent.Router.Models;

/// <summary>HTTP status values carried by <see cref="AgentRoutingException"/>; Core does not reference ASP.NET Core.</summary>
internal static class RouterStatusCodes
{
    internal const int BadRequest = 400;
    internal const int NotFound = 404;
    internal const int Conflict = 409;
    internal const int ServiceUnavailable = 503;
}
