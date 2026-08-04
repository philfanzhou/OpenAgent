using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Engine.Host.Middleware;

internal sealed class ProblemDetailsFactory
{
    internal ProblemDetails Create(
        string type,
        string title,
        int status,
        string detail,
        string? instance,
        string traceId,
        params (string Key, object Value)[] extensions)
    {
        var problemDetails = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            Instance = instance
        };
        problemDetails.Extensions["traceId"] = traceId;
        problemDetails.Extensions["timestamp"] = DateTimeOffset.UtcNow;
        foreach (var (key, value) in extensions)
        {
            problemDetails.Extensions[key] = value;
        }

        return problemDetails;
    }
}
