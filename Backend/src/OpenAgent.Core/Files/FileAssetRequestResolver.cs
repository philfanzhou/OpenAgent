using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetRequestResolver(IFileAssetService files)
{
    internal async Task<AgentRequest> ResolveAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (request.FileIds.Count == 0)
        {
            return request;
        }

        FileAssetScope scope = new()
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            ConversationId = request.ConversationId
        };
        List<AgentAttachment> attachments = request.Attachments.ToList();
        foreach (string fileId in request.FileIds.Distinct(StringComparer.Ordinal))
        {
            FileAssetContent content = await files.ReadAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
            attachments.Add(new AgentAttachment
            {
                FileId = content.Asset.FileId,
                FileName = content.Asset.FileName,
                MediaType = content.Asset.MediaType,
                Data = content.Data
            });
        }

        await files.AttachToConversationAsync(
            request.FileIds,
            request.ConversationId,
            cancellationToken).ConfigureAwait(false);
        return CopyWithAttachments(request, attachments);
    }

    private static AgentRequest CopyWithAttachments(
        AgentRequest request,
        IReadOnlyList<AgentAttachment> attachments) => new()
    {
        Query = request.Query,
        AgentId = request.AgentId,
        ConversationId = request.ConversationId,
        TraceId = request.TraceId,
        ClientType = request.ClientType,
        IdempotencyKey = request.IdempotencyKey,
        ExternalContext = request.ExternalContext,
        FileIds = request.FileIds,
        Attachments = attachments
    };
}
