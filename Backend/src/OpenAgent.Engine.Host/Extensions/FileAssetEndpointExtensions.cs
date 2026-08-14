using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class FileAssetEndpointExtensions
{
    internal static void MapFileAssets(this RouteGroupBuilder group)
    {
        group.MapPost("/files", UploadAsync)
            .DisableAntiforgery()
            .WithName("UploadFileAsset")
            .WithTags("File");
        group.MapGet("/files/{fileId}", GetAsync)
            .WithName("GetFileAsset")
            .WithTags("File");
        group.MapGet("/files/{fileId}/content", ContentAsync)
            .WithName("GetFileAssetContent")
            .WithTags("File");
        group.MapGet("/files/{fileId}/download", DownloadAsync)
            .WithName("DownloadFileAsset")
            .WithTags("File");
    }

    private static async Task<IResult> UploadAsync(
        [FromServices] IFileAssetService files,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files.Count != 1)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "Exactly one file is required.");
        }

        IFormFile file = form.Files[0];
        await using Stream content = file.OpenReadStream();
        FileAsset asset = await files.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = file.FileName,
                MediaType = file.ContentType,
                Source = FileAssetSource.UserUpload
            },
            content,
            CreateScope(context, conversationId: null),
            cancellationToken).ConfigureAwait(false);
        return Results.Created($"/api/v1/agent/files/{asset.FileId}", ToResponse(asset));
    }

    private static async Task<IResult> GetAsync(
        [FromServices] IFileAssetService files,
        string fileId,
        CancellationToken cancellationToken)
    {
        FileAsset? asset = await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false);
        return asset == null ? Results.NotFound() : Results.Ok(ToResponse(asset));
    }

    private static async Task<IResult> ContentAsync(
        [FromServices] IFileAssetService files,
        HttpContext context,
        string fileId,
        [FromQuery] string? conversationId,
        CancellationToken cancellationToken)
    {
        FileAssetContent content = await files.ReadAsync(
            fileId,
            CreateScope(context, conversationId),
            cancellationToken).ConfigureAwait(false);
        return Results.File(content.Data, content.Asset.MediaType, enableRangeProcessing: false);
    }

    private static async Task<IResult> DownloadAsync(
        [FromServices] IFileAssetService files,
        HttpContext context,
        string fileId,
        [FromQuery] string? conversationId,
        CancellationToken cancellationToken)
    {
        FileAssetContent content = await files.ReadAsync(
            fileId,
            CreateScope(context, conversationId),
            cancellationToken).ConfigureAwait(false);
        return Results.File(
            content.Data,
            content.Asset.MediaType,
            content.Asset.FileName,
            enableRangeProcessing: false);
    }

    private static FileAssetScope CreateScope(HttpContext context, string? conversationId) => new()
    {
        TenantId = AgentEndpointRequestMapper.RequireTenant(context),
        UserId = context.GetAgentRequest().User.UserId,
        ConversationId = conversationId
    };

    private static object ToResponse(FileAsset asset) => new
    {
        asset.FileId,
        asset.TenantId,
        asset.OwnerUserId,
        asset.FileName,
        asset.MediaType,
        asset.Length,
        asset.Sha256,
        asset.Source,
        asset.State,
        asset.CreatedAt
    };
}
