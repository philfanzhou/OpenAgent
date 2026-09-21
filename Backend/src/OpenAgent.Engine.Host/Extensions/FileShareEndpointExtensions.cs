using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Responses;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class FileShareEndpointExtensions
{
    /// <summary>
    /// 匿名分享下载端点。链接令牌本身就是凭证，不要求登录，也不经过
    /// /api/v1/agent 的租户上下文；所有校验（过期、次数）在 IFileShareService 内完成。
    /// </summary>
    internal static IEndpointConventionBuilder MapFileShareDownloads(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet(
            $"{IFileShareService.RoutePrefix}/{{token}}",
            async Task<Results<FileContentHttpResult, NotFound>> ([FromServices] IFileShareService shares, string token, CancellationToken cancellationToken) =>
            {
                FileShareRedemption? redemption = await shares.RedeemAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                // 链接不存在、已过期或已用尽下载次数统一返回 404，避免暴露链接状态。
                return redemption == null
                    ? TypedResults.NotFound()
                    : TypedResults.File(
                        redemption.Data,
                        redemption.MediaType,
                        redemption.FileName,
                        enableRangeProcessing: false);
            })
            .WithName("DownloadSharedFile")
            .WithTags("File")
            .WithSummary("凭分享令牌下载文件（匿名）");
    }

    internal static void MapFileShareLinks(this RouteGroupBuilder group)
    {
        group.MapPost("/files/{fileId}/share", CreateAsync)
            .DisableAntiforgery()
            .WithName("CreateFileShareLink")
            .WithTags("File")
            .WithSummary("创建文件分享链接");
        group.MapGet("/files/shares", ListAsync)
            .WithName("ListFileShareLinks")
            .WithTags("File")
            .WithSummary("列出当前用户的有效分享链接");
        group.MapDelete("/files/shares/{shareId}", RevokeAsync)
            .WithName("RevokeFileShareLink")
            .WithTags("File")
            .WithSummary("撤销分享链接");
    }

    private static async Task<Ok<FileShareLinkResponse>> CreateAsync(
        [FromServices] IFileShareService shares,
        [FromBody] FileShareCreateRequest? request,
        HttpContext context,
        string fileId,
        CancellationToken cancellationToken)
    {
        // mode/audience 均可选：不指定时走 audience 默认策略（REST 是用户入口，默认 user：3 天、不限次）。
        FileShareMode? mode = null;
        string? modeText = request?.Mode;
        if (!string.IsNullOrWhiteSpace(modeText))
        {
            if (!FileShareModeParser.TryParse(modeText, out FileShareMode parsedMode))
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "Mode must be one of: temporary, singleUse, longTerm.");
            }
            mode = parsedMode;
        }

        FileShareAudience? audience = null;
        string? audienceText = request?.Audience;
        if (!string.IsNullOrWhiteSpace(audienceText))
        {
            if (!FileShareAudienceParser.TryParse(audienceText, out FileShareAudience parsedAudience))
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "Audience must be one of: mcp, user.");
            }
            audience = parsedAudience;
        }

        FileShareLink share = await shares.CreateAsync(
            fileId,
            CreateScope(context),
            new FileShareRequest { Mode = mode, Audience = audience, ExpiresInSeconds = request?.ExpiresInSeconds },
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new FileShareLinkResponse
        {
            ShareId = share.ShareId,
            FileId = share.FileId,
            FileName = share.FileName,
            MediaType = share.MediaType,
            Length = share.Length,
            Mode = share.Mode.ToString(),
            Url = ResolveAbsoluteUrl(share.Url, context),
            ExpiresAt = share.ExpiresAt,
            MaxDownloads = share.MaxDownloads,
            DownloadCount = share.DownloadCount
        });
    }

    private static async Task<IReadOnlyList<FileShareLinkResponse>> ListAsync(
        [FromServices] IFileShareService shares,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileShareSummary> items = await shares.ListAsync(
            CreateScope(context),
            cancellationToken).ConfigureAwait(false);
        return items.Select(share => new FileShareLinkResponse
        {
            ShareId = share.ShareId,
            FileId = share.FileId,
            FileName = share.FileName,
            MediaType = share.MediaType,
            Length = share.Length,
            Mode = share.Mode.ToString(),
            ExpiresAt = share.ExpiresAt,
            MaxDownloads = share.MaxDownloads,
            DownloadCount = share.DownloadCount,
            CreatedAt = share.CreatedAt
        }).ToList();
    }

    private static async Task<Results<NoContent, NotFound>> RevokeAsync(
        [FromServices] IFileShareService shares,
        HttpContext context,
        string shareId,
        CancellationToken cancellationToken)
    {
        bool revoked = await shares.RevokeAsync(
            shareId,
            CreateScope(context),
            cancellationToken).ConfigureAwait(false);
        // 不存在、已失效与不属于当前用户统一 404，避免分享 ID 被探测。
        return revoked ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static FileAssetScope CreateScope(HttpContext context) => new()
    {
        TenantId = AgentEndpointRequestMapper.RequireTenant(context),
        UserId = context.GetAgentRequest().User.UserId
    };

    /// <summary>未配置 PublicBaseUrl 时用当前请求 origin 拼出绝对地址（反向代理场景以配置为准）。</summary>
    private static string ResolveAbsoluteUrl(string url, HttpContext context) =>
        url.StartsWith('/')
            ? $"{context.Request.Scheme}://{context.Request.Host}{url}"
            : url;

    internal sealed record FileShareCreateRequest(string? Mode, int? ExpiresInSeconds, string? Audience);
}
