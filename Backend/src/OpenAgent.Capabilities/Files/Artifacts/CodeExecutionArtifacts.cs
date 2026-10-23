using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Files.Artifacts;

/// <summary>
/// Publishes Runner output files as conversation-owned FileAssets. Neither the sandbox
/// nor storage filters output types: every non-empty output is registered with the media
/// type inferred from its extension (octet-stream for extensions outside the catalog).
/// Outputs the storage layer still rejects (e.g. over the size limit) are skipped and
/// reported so the model can retry instead of losing the whole execution result. Shared
/// by execute_code and skill script runs so both register artifacts identically.
/// </summary>
internal static class CodeExecutionArtifacts
{
    internal sealed record SkippedFile(string Name, string Reason);

    internal sealed record PublishResult(List<object> Files, List<SkippedFile> Skipped);

    internal static async Task<PublishResult> PublishAsync(
        CodeExecutionResult result,
        IFileAssetService files,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<object>();
        var skipped = new List<SkippedFile>();
        foreach (ExecutionFile output in result.Files)
        {
            if (output.Content.Length == 0)
            {
                skipped.Add(new SkippedFile(output.Name, "Generated files must not be empty."));
                continue;
            }
            try
            {
                await using var stream = new MemoryStream(output.Content, writable: false);
                // 不传 MediaType：服务端按扩展名推断规范类型，目录外扩展名记为 octet-stream。
                FileAsset asset = await files.UploadAsync(new FileAssetCreateRequest
                {
                    FileName = output.Name,
                    Source = FileAssetSource.Agent
                }, stream, scope, cancellationToken).ConfigureAwait(false);
                await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
                artifacts.Add(new { fileId = asset.FileId, fileName = asset.FileName, length = asset.Length });
            }
            catch (AgentException exception)
            {
                // 存储层拒绝（运维收窄白名单、超限等）：记录原因并继续注册其余产物。
                skipped.Add(new SkippedFile(output.Name, exception.Message));
            }
        }
        return new PublishResult(artifacts, skipped);
    }
}
