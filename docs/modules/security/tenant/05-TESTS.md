# TenantValidation — 测试覆盖

## 测试文件

`test/OpenAgent.Core.Tests/Middleware/TenantValidationTests.cs`

## 测试用例

| 用例 | 方法 | 说明 |
|------|------|------|
| `InvokeAsync_passes_through_when_TenantId_is_present` | `InvokeAsync` | TenantId 存在时，请求正常通过管道 |
| `InvokeAsync_throws_TenantDataIsolationException_when_TenantId_is_missing` | `InvokeAsync` | TenantId 为 null 时，抛出 `TenantDataIsolationException`，ErrorCode 为 `TenantDataIsolationViolation` |
| `InvokeStreamAsync_throws_TenantDataIsolationException_when_TenantId_is_missing` | `InvokeStreamAsync` | 流式路径下，TenantId 为 null 时抛出 `TenantDataIsolationException` |

## 测试基础设施

### 辅助方法

| 方法 | 说明 |
|------|------|
| `CreateRequest()` | 创建测试用 `AgentRequest`（Query="test query", AgentId="agent-1"） |
| `CreateUserContext(tenantId)` | 创建测试用 `AgentUserContext`（UserId="user-1", TenantId 参数控制, IsAuthenticated=true） |
| `NextDelegate` | 返回 `AgentResponse { Content="next-called", Success=true }` |
| `StreamNextDelegate` | 返回单个 chunk `"chunk-1"` |

## 错误码验证

| 错误码 | 值 | 测试覆盖 |
|--------|-----|---------|
| `TenantDataIsolationViolation` | 5003 | ✅ `InvokeAsync_throws_TenantDataIsolationException_when_TenantId_is_missing` |
