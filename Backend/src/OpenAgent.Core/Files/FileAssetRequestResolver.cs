using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetRequestResolver
{
    private readonly IFileAssetService _files;

    public FileAssetRequestResolver(IFileAssetService files)
    {
        _files = files;
    }

    internal async Task<ResolvedFileRequest> ResolveAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (request.FileIds.Count == 0)
        {
            return new ResolvedFileRequest { Request = request, Files = Array.Empty<FileAsset>() };
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

        return new ResolvedFileRequest { Request = request, Files = files.AsReadOnly() };
    }
}
