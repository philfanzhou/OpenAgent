using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OpenAgent.Hosting.Authentication;

internal sealed class AgentAuthenticationOptionsValidator(IHostEnvironment? environment)
    : IValidateOptions<AgentAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentAuthenticationOptions options)
    {
        if (options.Mode == AgentAuthenticationMode.Basic
            && environment != null
            && !environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                "Basic authentication is restricted to the Development environment.");
        }

        if (options.Mode == AgentAuthenticationMode.JwtBearer)
        {
            if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out Uri? authority))
            {
                return ValidateOptionsResult.Fail(
                    "JWT Bearer Authority must be an absolute HTTP(S) URI.");
            }

            if (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp)
            {
                return ValidateOptionsResult.Fail(
                    "JWT Bearer Authority must be an absolute HTTP(S) URI.");
            }

            if (options.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
            {
                return ValidateOptionsResult.Fail(
                    "JWT Bearer Authority must use HTTPS when RequireHttpsMetadata is enabled.");
            }

            if (!options.RequireHttpsMetadata
                && authority.Scheme == Uri.UriSchemeHttp
                && authority.Host is not "localhost" and not "127.0.0.1")
            {
                return ValidateOptionsResult.Fail(
                    "Insecure JWT metadata is permitted only for local Development endpoints.");
            }

            if (!options.RequireHttpsMetadata
                && environment != null
                && !environment.IsDevelopment())
            {
                return ValidateOptionsResult.Fail(
                    "Production JWT metadata must require HTTPS.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
