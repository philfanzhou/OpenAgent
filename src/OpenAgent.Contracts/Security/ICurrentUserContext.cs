using System.Security.Claims;

namespace OpenAgent.Contracts.Security;

/// <summary>
/// 当前请求的用户上下文，由 DI 容器提供 Scoped 实例。
/// 任何需要用户身份/权限的服务均可注入此接口，无需在方法签名中透传 userId。
/// </summary>
public interface ICurrentUserContext
{
    string UserId { get; }
    string? TenantId { get; }
    bool IsAuthenticated { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsInRole(string role);
}
