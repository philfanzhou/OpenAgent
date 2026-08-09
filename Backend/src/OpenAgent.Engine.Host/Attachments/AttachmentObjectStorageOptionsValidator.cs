using Microsoft.Extensions.Options;

namespace OpenAgent.Engine.Host.Attachments;

internal sealed class AttachmentObjectStorageOptionsValidator
    : IValidateOptions<AttachmentObjectStorageOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AttachmentObjectStorageOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (!IsValidBucketName(options.BucketName))
        {
            failures.Add("Attachments:ObjectStorage:BucketName must use 3 to 63 lowercase letters, digits, dots, or hyphens and start and end with a letter or digit.");
        }
        if (string.IsNullOrWhiteSpace(options.Region))
        {
            failures.Add("Attachments:ObjectStorage:Region is required.");
        }
        if (!IsValidKeyPrefix(options.KeyPrefix))
        {
            failures.Add("Attachments:ObjectStorage:KeyPrefix must contain only safe, non-empty path segments.");
        }
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl)
            && (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out Uri? serviceUrl)
                || (serviceUrl.Scheme != Uri.UriSchemeHttp
                    && serviceUrl.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(serviceUrl.Host)
                || !string.IsNullOrEmpty(serviceUrl.UserInfo)
                || !string.IsNullOrEmpty(serviceUrl.Query)
                || !string.IsNullOrEmpty(serviceUrl.Fragment)))
        {
            failures.Add("Attachments:ObjectStorage:ServiceUrl must be an absolute HTTP(S) origin without credentials, query, or fragment.");
        }

        bool hasAccessKey = !string.IsNullOrWhiteSpace(options.AccessKey);
        bool hasSecretKey = !string.IsNullOrWhiteSpace(options.SecretKey);
        if (hasAccessKey != hasSecretKey)
        {
            failures.Add("Attachments:ObjectStorage:AccessKey and SecretKey must be configured together.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidBucketName(string bucketName) =>
        !string.IsNullOrWhiteSpace(bucketName)
        && bucketName.Length is >= 3 and <= 63
        && IsLowercaseLetterOrDigit(bucketName[0])
        && IsLowercaseLetterOrDigit(bucketName[^1])
        && bucketName.All(character =>
            IsLowercaseLetterOrDigit(character) || character is '.' or '-');

    private static bool IsLowercaseLetterOrDigit(char character) =>
        char.IsAsciiDigit(character) || character is >= 'a' and <= 'z';

    private static bool IsValidKeyPrefix(string keyPrefix)
    {
        string normalized = keyPrefix.Trim('/');
        if (normalized.Length == 0)
        {
            return string.IsNullOrEmpty(keyPrefix) || keyPrefix.All(character => character == '/');
        }

        return normalized.Split('/').All(segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && segment.All(IsSafeKeyCharacter));
    }

    private static bool IsSafeKeyCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~';
}
