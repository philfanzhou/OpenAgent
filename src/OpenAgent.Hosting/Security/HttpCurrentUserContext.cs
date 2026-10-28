using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Hosting.Security;

/// <summary>
/// 基于 <see cref="IHttpContextAccessor"/> 的懒解析实现。
/// 从当前 HTTP 请求的 <see cref="ClaimsPrincipal"/> 读取用户身份，
/// 无需中间件预填充，认证中间件执行后即可访问。
/// </summary>
internal sealed class HttpCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUserContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public string UserId =>
        Principal?.FindFirst("sub")?.Value
        ?? Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Principal?.Identity?.Name
        ?? "anonymous";

    public string? TenantId =>
        Principal?.Claims.FirstOrDefault(c => c.Type is "tenant_id" or "tid")?.Value;

    public bool IsAuthenticated =>
        Principal?.Identity?.IsAuthenticated ?? false;

    public IReadOnlyList<string> Roles =>
        Principal?.Claims
            .Where(c => c.Type is ClaimTypes.Role or "roles" or "role")
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    public bool IsInRole(string role) =>
        Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}
