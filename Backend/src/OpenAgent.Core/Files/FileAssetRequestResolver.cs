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
            return new ResolvedFileRequest { Request = request, Files = Array.Empty<FileAssetContent>() };
        }

        FileAssetScope scope = new()
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            ConversationId = request.ConversationId
        };
        await _files.EnsureReferencesAsync(request.FileIds, scope, cancellationToken).ConfigureAwait(false);
        List<FileAssetContent> files = [];
        foreach (string fileId in request.FileIds.Distinct(StringComparer.Ordinal))
        {
            FileAssetContent content = await _files.ReadAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
            files.Add(content);
        }

        return new ResolvedFileRequest { Request = request, Files = files.AsReadOnly() };
    }
}
