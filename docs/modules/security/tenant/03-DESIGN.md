# TenantValidation — 数据模型

## TenantValidation

租户校验中间件实现类（internal，`OpenAgent.Core.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"TenantValidation"`（`nameof(TenantValidation)`） |

构造函数依赖：
- `ILogger<TenantValidation> _logger` — 日志记录器

## TenantDataIsolationException

租户数据隔离异常（`OpenAgent.Contracts.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `ErrorCode` | `AgentErrorCode` | 固定为 `TenantDataIsolationViolation` (5003) |
| `TenantId` | `string?` | 用户的租户 ID（此处为 null） |
| `RequestedTenantId` | `string?` | 请求的租户 ID（此处为 null） |
| `Message` | `string?` | `"TenantId is required but not provided"` |
| `Details` | `string?` | `"{tenantId} vs {requestedTenantId}"`（此处为 " vs "） |

继承关系：`TenantDataIsolationException` → `AgentException` → `Exception`

## AgentErrorCode 租户相关值

| 错误码 | 值 | 说明 |
|--------|-----|------|
| `TenantMismatch` | 5001 | 租户不匹配 |
| `TenantNotFound` | 5002 | 租户未找到 |
| `TenantDataIsolationViolation` | 5003 | 租户数据隔离违规 |
