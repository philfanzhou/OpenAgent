using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Hosting.Security;

/// <summary>
/// 基于 <see cref="IHttpContextAccessor"/> 的懒解析实现。
/// 从当前 HTTP 请求的 <see cref="ClaimsPrincipal"/> 读取用户身份，
/// 无需中间件预填充，认证中间件执行后即可访问。
/// </summary>
internal sealed class HttpCurrentUserContext : ICurrentUserContext, ICurrentUserContextInitializer
{
    private readonly IHttpContextAccessor _accessor;
    private IAgentUserContext? _executionUser;

    public HttpCurrentUserContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public void Initialize(IAgentUserContext user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_executionUser != null) throw new InvalidOperationException("Execution identity is already initialized.");
        _executionUser = user;
    }

    public string UserId =>
        _executionUser?.UserId
        ?? Principal?.FindFirst("sub")?.Value
        ?? Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Principal?.Identity?.Name
        ?? string.Empty;

    public string? TenantId =>
        _executionUser is { } user ? user.TenantId
        : Principal?.Claims.FirstOrDefault(c => c.Type is "tenant_id" or "tid")?.Value;

    public bool IsAuthenticated =>
        _executionUser?.IsAuthenticated ?? Principal?.Identity?.IsAuthenticated ?? false;

    public IReadOnlyList<string> Roles =>
        _executionUser?.Roles ?? Principal?.Claims
            .Where(c => c.Type is ClaimTypes.Role or "roles" or "role")
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    public bool IsInRole(string role) =>
        Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}
