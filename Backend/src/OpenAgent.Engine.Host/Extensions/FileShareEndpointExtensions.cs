using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
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
            async ([FromServices] IFileShareService shares, string token, CancellationToken cancellationToken) =>
            {
                FileShareRedemption? redemption = await shares.RedeemAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                // 链接不存在、已过期或已用尽下载次数统一返回 404，避免暴露链接状态。
                return redemption == null
                    ? Results.NotFound()
                    : Results.File(
                        redemption.Data,
                        redemption.MediaType,
                        redemption.FileName,
                        enableRangeProcessing: false);
            })
            .WithName("DownloadSharedFile")
            .WithTags("File");
    }

    internal static void MapFileShareLinks(this RouteGroupBuilder group)
    {
        group.MapPost("/files/{fileId}/share", CreateAsync)
            .DisableAntiforgery()
            .WithName("CreateFileShareLink")
            .WithTags("File");
    }

    private static async Task<IResult> CreateAsync(
        [FromServices] IFileShareService shares,
        [FromBody] FileShareCreateRequest? request,
        HttpContext context,
        string fileId,
        CancellationToken cancellationToken)
    {
        FileShareMode? mode = FileShareModeParser.Parse(request?.Mode);
        if (mode == null)
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "Mode must be one of: temporary, singleUse, longTerm.");
        }

        FileShareLink share = await shares.CreateAsync(
            fileId,
            new FileAssetScope
            {
                TenantId = AgentEndpointRequestMapper.RequireTenant(context),
                UserId = context.GetAgentRequest().User.UserId
            },
            new FileShareRequest { Mode = mode.Value, ExpiresInSeconds = request?.ExpiresInSeconds },
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            share.FileId,
            share.FileName,
            share.MediaType,
            share.Length,
            mode = share.Mode.ToString(),
            url = ResolveAbsoluteUrl(share.Url, context),
            share.ExpiresAt,
            share.MaxDownloads,
            share.DownloadCount
        });
    }

    /// <summary>未配置 PublicBaseUrl 时用当前请求 origin 拼出绝对地址（反向代理场景以配置为准）。</summary>
    private static string ResolveAbsoluteUrl(string url, HttpContext context) =>
        url.StartsWith('/')
            ? $"{context.Request.Scheme}://{context.Request.Host}{url}"
            : url;

    internal sealed record FileShareCreateRequest(string? Mode, int? ExpiresInSeconds);
}
