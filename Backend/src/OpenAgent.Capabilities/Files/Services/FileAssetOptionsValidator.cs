using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Capabilities.Files.Services;

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
        if (options.MaxInlineImageBytes <= 0)
        {
            failures.Add("FileAssets:MaxInlineImageBytes must be greater than zero.");
        }
        if (options.MaxInlineImageCount <= 0)
        {
            failures.Add("FileAssets:MaxInlineImageCount must be greater than zero.");
        }
        if (options.InlineImageMaxLongEdge < 0)
        {
            failures.Add("FileAssets:InlineImageMaxLongEdge must be greater than or equal to zero.");
        }
        if (options.InlineImageMaxPixels <= 0)
        {
            failures.Add("FileAssets:InlineImageMaxPixels must be greater than zero.");
        }
        if (options.InlineImageMaxDecodedBytes <= 0)
        {
            failures.Add("FileAssets:InlineImageMaxDecodedBytes must be greater than zero.");
        }
        if (options.InlineImageQuality is < 1 or > 100)
        {
            failures.Add("FileAssets:InlineImageQuality must be between 1 and 100.");
        }
        if (options.InlineImageHistoryTurns < 0)
        {
            failures.Add("FileAssets:InlineImageHistoryTurns must be greater than or equal to zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
