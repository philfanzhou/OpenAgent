using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

namespace OpenAgent.Router.Endpoints;

internal static partial class RegistrationEndpointHandler
{
    internal const string SecretConfigKey = "RouterSettings:Registration:SharedSecret";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex InstanceIdPattern();

    internal static async Task<IResult> PutAsync(
        string instanceId,
        RegistrationRequest request,
        HttpContext context,
        IConfiguration configuration,
        [FromServices] EngineRegistryWriter? writer)
    {
        if (!IsAuthorized(context, configuration))
        {
            return Results.Unauthorized();
        }

        if (writer == null)
        {
            return Results.Problem("Registration requires Redis.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!InstanceIdPattern().IsMatch(instanceId))
        {
            return Results.BadRequest("Invalid instance id.");
        }

        // Discovery builds http://host:port endpoints, so https and path prefixes cannot be honored.
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out Uri? baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttp
            || baseUrl.AbsolutePath != "/")
        {
            return Results.BadRequest("baseUrl must be an absolute http URL without a path.");
        }

        await writer.PutAsync(instanceId, baseUrl, request).ConfigureAwait(false);
        return Results.NoContent();
    }

    internal static async Task<IResult> DeleteAsync(
        string instanceId,
        HttpContext context,
        IConfiguration configuration,
        [FromServices] EngineRegistryWriter? writer)
    {
        if (!IsAuthorized(context, configuration))
        {
            return Results.Unauthorized();
        }

        if (writer == null)
        {
            return Results.Problem("Registration requires Redis.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!InstanceIdPattern().IsMatch(instanceId))
        {
            return Results.BadRequest("Invalid instance id.");
        }

        await writer.RemoveAsync(instanceId).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        string? secret = configuration[SecretConfigKey];
        string? presented = context.Request.Headers[RegistrationRequest.KeyHeader];
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }
}
