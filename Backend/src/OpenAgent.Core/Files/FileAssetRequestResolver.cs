using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using Microsoft.Extensions.Options;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetRequestResolver
{
    private readonly IFileAssetService _files;
    private readonly FileAssetOptions _options;

    public FileAssetRequestResolver(IFileAssetService files, IOptions<FileAssetOptions> options)
    {
        _files = files;
        _options = options.Value;
    }

    internal async Task<ResolvedFileRequest> ResolveAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken,
        bool supportsVision = false)
    {
        if (request.FileIds.Count == 0)
        {
            return new ResolvedFileRequest
            {
                Request = request,
                Files = Array.Empty<FileAsset>(),
                InlineImages = Array.Empty<FileAssetContent>()
            };
        }

        FileAssetScope scope = new()
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            ConversationId = request.ConversationId
        };
        await _files.EnsureReferencesAsync(request.FileIds, scope, cancellationToken).ConfigureAwait(false);
        List<FileAsset> files = [];
        foreach (string fileId in request.FileIds.Distinct(StringComparer.Ordinal))
        {
            FileAsset? asset = await _files.GetReferencedAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
            if (asset == null)
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, $"File '{fileId}' was not found.");
            }

            files.Add(asset);
        }

        List<FileAssetContent> inlineImages = [];
        if (supportsVision)
        {
            foreach (FileAsset asset in files
                .Where(asset => IsImage(asset.MediaType)
                    && asset.Length <= _options.MaxInlineImageBytes)
                .Take(_options.MaxInlineImageCount))
            {
                try
                {
                    inlineImages.Add(await _files.ReadAsync(
                        asset.FileId,
                        scope,
                        cancellationToken,
                        _options.MaxInlineImageBytes).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AgentException)
                {
                    // Inline vision input is an optimization; retain the manifest and
                    // let the model use a file-analysis tool when the object is unavailable.
                }
            }
        }

        return new ResolvedFileRequest
        {
            Request = request,
            Files = files.AsReadOnly(),
            InlineImages = inlineImages.AsReadOnly()
        };
    }

    private static bool IsImage(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
