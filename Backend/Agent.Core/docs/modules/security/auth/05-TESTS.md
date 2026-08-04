# Auth — 测试覆盖

## 测试文件

`test/OpenAgent.Core.Tests/Middleware/AuthTests.cs`

## 测试用例

| 用例 | 方法 | 说明 |
|------|------|------|
| `InvokeAsync_passes_through_when_authenticated` | `InvokeAsync` | 用户已认证时，请求正常通过管道，返回 next 委托的响应 |
| `InvokeAsync_throws_AgentException_when_not_authenticated` | `InvokeAsync` | 用户未认证时，抛出 `AgentException`，ErrorCode 为 `PermissionDenied` |
| `InvokeStreamAsync_throws_AgentException_when_not_authenticated` | `InvokeStreamAsync` | 流式路径下，用户未认证时抛出 `AgentException` |

## 测试基础设施

### FakePermissionEvaluator

测试内嵌的 `IPermissionEvaluator` 桩实现，通过构造函数参数 `isAuthenticated` 控制返回值。

### 辅助方法

| 方法 | 说明 |
|------|------|
| `CreateRequest()` | 创建测试用 `AgentRequest`（Query="test query", AgentId="agent-1"） |
| `CreateUserContext(isAuthenticated)` | 创建测试用 `AgentUserContext`（UserId="user-1", TenantId="tenant-1"） |
| `NextDelegate` | 返回 `AgentResponse { Content="next-called", Success=true }` |
| `StreamNextDelegate` | 返回单个 chunk `"chunk-1"` |

## 错误码验证

| 错误码 | 值 | 测试覆盖 |
|--------|-----|---------|
| `PermissionDenied` | 100 | ✅ `InvokeAsync_throws_AgentException_when_not_authenticated` |
