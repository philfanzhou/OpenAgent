namespace OpenAgent.Engine.Host.Extensions;

internal static class EndpointExtensions
{
    public static IEndpointConventionBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/agent")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();
        group.MapAgentChat();
        group.MapAttachmentChat();
        group.MapAgentCatalog();
        group.MapConversations();
        return group;
    }
}
