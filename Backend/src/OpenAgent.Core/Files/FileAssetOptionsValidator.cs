using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetOptionsValidator : IValidateOptions<FileAssetOptions>
{
    public ValidateOptionsResult Validate(string? name, FileAssetOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (options.MaxFileSizeBytes <= 0)
        {
            failures.Add("FileAssets:MaxFileSizeBytes must be greater than zero.");
        }
        if (options.MaxFunctionReadBytes <= 0)
        {
            failures.Add("FileAssets:MaxFunctionReadBytes must be greater than zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
