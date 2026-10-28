namespace OpenAgent.Router.Models;

internal sealed class AgentRoutingException(
    int statusCode,
    string code,
    string title) : Exception(title)
{
    internal int StatusCode { get; } = statusCode;

    internal string Code { get; } = code;

    internal string Title { get; } = title;
}
