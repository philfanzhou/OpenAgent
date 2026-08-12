using Microsoft.Extensions.Options;

namespace OpenAgent.Engine.Host.Files;

internal sealed class FileObjectStorageOptionsValidator : IValidateOptions<FileObjectStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FileObjectStorageOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            failures.Add("FileAssets:ObjectStorage:BucketName is required.");
        }
        if (string.IsNullOrWhiteSpace(options.Region))
        {
            failures.Add("FileAssets:ObjectStorage:Region is required.");
        }
        if (string.IsNullOrWhiteSpace(options.KeyPrefix)
            || options.KeyPrefix.Split('/').Any(segment => segment is "." or ".." or { Length: 0 }))
        {
            failures.Add("FileAssets:ObjectStorage:KeyPrefix must contain safe path segments.");
        }
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl)
            && (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out Uri? serviceUrl)
                || (serviceUrl.Scheme != Uri.UriSchemeHttp && serviceUrl.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(serviceUrl.Host)
                || !string.IsNullOrEmpty(serviceUrl.UserInfo)
                || !string.IsNullOrEmpty(serviceUrl.Query)
                || !string.IsNullOrEmpty(serviceUrl.Fragment)))
        {
            failures.Add("FileAssets:ObjectStorage:ServiceUrl must be an HTTP(S) origin without credentials, query, or fragment.");
        }

        bool hasAccessKey = !string.IsNullOrWhiteSpace(options.AccessKey);
        bool hasSecretKey = !string.IsNullOrWhiteSpace(options.SecretKey);
        if (hasAccessKey != hasSecretKey)
        {
            failures.Add("FileAssets:ObjectStorage:AccessKey and SecretKey must be configured together.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
