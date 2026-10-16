namespace OpenAgent.Router;

internal interface IAgentForwarder
{
    Task ForwardAsync(
        HttpContext context,
        IAgentProvider provider,
        string? action,
        CancellationToken cancellationToken);
}
