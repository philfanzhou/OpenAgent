using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Responses;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Hosting.Errors;

namespace OpenAgent.Engine.Host.Extensions;

internal static class FileAssetEndpointExtensions
{
    internal static void MapFileAssets(this RouteGroupBuilder group)
    {
        group.MapPost("/files", UploadAsync)
            .DisableAntiforgery()
            .WithName("UploadFileAsset")
            .WithTags("File");
        // 字面路由 /files/object 必须先于参数路由 /files/{fileId} 注册，
        // 不依赖路由优先级兜底。
        group.MapGet("/files/object", ObjectContentAsync)
            .WithName("GetObjectAssetContent")
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
        group.MapFileShareLinks();
    }

    private static async Task<Created<FileAssetResponse>> UploadAsync(
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
        return TypedResults.Created($"/api/v1/agent/files/{asset.FileId}", ToResponse(asset));
    }

    private static async Task<Results<Ok<FileAssetResponse>, ProblemHttpResult>> GetAsync(
        [FromServices] IFileAssetService files,
        HttpContext context,
        string fileId,
        CancellationToken cancellationToken)
    {
        FileAsset? asset = await files.GetAsync(
            fileId,
            CreateScope(context, conversationId: null),
            cancellationToken).ConfigureAwait(false);
        // 404 同样走统一 ProblemDetails 契约，不返回空 body 的裸 NotFound。
        return asset == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"File '{fileId}' was not found.", context))
            : TypedResults.Ok(ToResponse(asset));
    }

    private static async Task<FileContentHttpResult> ContentAsync(
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
        AddNoSniff(context);
        return TypedResults.File(content.Data, content.Asset.MediaType, enableRangeProcessing: false);
    }

    private static async Task<FileContentHttpResult> ObjectContentAsync(
        [FromServices] IFileAssetService files,
        HttpContext context,
        [FromQuery] string? path,
        [FromQuery] string? conversationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "Path query parameter is required.");
        }
        byte[] content = await files.ReadObjectAsync(
            path,
            CreateScope(context, conversationId),
            cancellationToken).ConfigureAwait(false);
        AddNoSniff(context);
        return TypedResults.File(content, InferContentType(path), enableRangeProcessing: false);
    }

    private static string InferContentType(string objectKey)
    {
        if (!FileMediaTypeCatalog.TryGetMediaType(Path.GetExtension(objectKey), out string mediaType))
        {
            return "application/octet-stream";
        }
        return FileMediaTypeCatalog.IsTextMediaType(mediaType)
            ? $"{mediaType}; charset=utf-8"
            : mediaType;
    }

    private static async Task<FileContentHttpResult> DownloadAsync(
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
        AddNoSniff(context);
        return TypedResults.File(
            content.Data,
            content.Asset.MediaType,
            content.Asset.FileName,
            enableRangeProcessing: false);
    }

    /// <summary>内容来自用户/模型的文件可能被浏览器嗅探执行；禁用 MIME 嗅探只按声明的 Content-Type 处理。</summary>
    private static void AddNoSniff(HttpContext context) =>
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

    private static FileAssetScope CreateScope(HttpContext context, string? conversationId) => new()
    {
        TenantId = AgentEndpointRequestMapper.RequireTenant(context),
        UserId = context.GetAgentRequest().User.UserId,
        ConversationId = conversationId
    };

    private static FileAssetResponse ToResponse(FileAsset asset) => new()
    {
        FileId = asset.FileId,
        TenantId = asset.TenantId,
        OwnerUserId = asset.OwnerUserId,
        FileName = asset.FileName,
        MediaType = asset.MediaType,
        Length = asset.Length,
        Sha256 = asset.Sha256,
        ObjectKey = asset.ObjectKey,
        Source = asset.Source,
        State = asset.State,
        CreatedAt = asset.CreatedAt
    };
}
