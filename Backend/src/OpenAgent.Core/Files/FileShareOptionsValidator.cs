using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Files;

internal sealed class FileShareOptionsValidator : IValidateOptions<FileShareOptions>
{
    public ValidateOptionsResult Validate(string? name, FileShareOptions options)
    {
        List<string> failures = [];
        if (options.TemporaryLifetimeSeconds <= 0)
        {
            failures.Add("FileAssets:Share:TemporaryLifetimeSeconds must be greater than zero.");
        }
        if (options.SingleUseLifetimeSeconds <= 0)
        {
            failures.Add("FileAssets:Share:SingleUseLifetimeSeconds must be greater than zero.");
        }
        if (options.LongTermLifetimeSeconds <= 0)
        {
            failures.Add("FileAssets:Share:LongTermLifetimeSeconds must be greater than zero.");
        }
        if (options.MaxLifetimeSeconds <= 0)
        {
            failures.Add("FileAssets:Share:MaxLifetimeSeconds must be greater than zero.");
        }
        if (Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? uri)
            && uri.Scheme != Uri.UriSchemeHttp
            && uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("FileAssets:Share:PublicBaseUrl must use http(s).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
