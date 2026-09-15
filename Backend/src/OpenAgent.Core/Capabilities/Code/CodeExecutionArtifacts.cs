using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Capabilities.Code;

/// <summary>
/// Publishes Runner output files as conversation-owned FileAssets and maps
/// supported output names to media types. Shared by execute_code and skill
/// script runs so both register artifacts identically.
/// </summary>
internal static class CodeExecutionArtifacts
{
    internal static string GetMediaType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        _ => throw new ArgumentException("Unsupported generated file type.")
    };

    internal static async Task<List<object>> PublishAsync(
        CodeExecutionResult result,
        IFileAssetService files,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        foreach (ExecutionFile output in result.Files)
        {
            _ = GetMediaType(output.Name);
            if (output.Content.Length == 0)
            {
                throw new ArgumentException("Generated files must not be empty.");
            }
        }
        var artifacts = new List<object>();
        foreach (ExecutionFile output in result.Files)
        {
            await using var stream = new MemoryStream(output.Content, writable: false);
            FileAsset asset = await files.UploadAsync(new FileAssetCreateRequest
            {
                FileName = output.Name,
                MediaType = GetMediaType(output.Name),
                Source = FileAssetSource.Agent
            }, stream, scope, cancellationToken).ConfigureAwait(false);
            await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new { fileId = asset.FileId, fileName = asset.FileName, length = asset.Length });
        }
        return artifacts;
    }
}
