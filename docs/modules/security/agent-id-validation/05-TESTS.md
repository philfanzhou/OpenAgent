# AgentIdValidation — 测试覆盖

## 测试文件

`test/OpenAgent.Core.Tests/Middleware/AgentIdValidationTests.cs`

## 测试用例

| 用例 | 方法 | 说明 |
|------|------|------|
| `InvokeAsync_passes_through_when_AgentId_is_present` | `InvokeAsync` | AgentId 存在时，请求正常通过管道，返回 next 委托的响应 |
| `InvokeAsync_passes_through_when_AgentId_is_missing` | `InvokeAsync` | AgentId 为 null 时，请求仍正常通过管道（不抛异常） |
| `InvokeStreamAsync_passes_through_when_AgentId_is_missing` | `InvokeStreamAsync` | 流式路径下，AgentId 为 null 时请求正常通过，返回所有 chunk |

## 测试基础设施

### 辅助方法

| 方法 | 说明 |
|------|------|
| `CreateRequest(agentId)` | 创建测试用 `AgentRequest`（Query="test query", AgentId 参数控制） |
| `CreateUserContext()` | 创建测试用 `AgentUserContext`（UserId="user-1", TenantId="tenant-1", IsAuthenticated=true） |
| `NextDelegate` | 返回 `AgentResponse { Content="next-called", Success=true }` |
| `StreamNextDelegate` | 返回两个 chunk `"chunk-1"`, `"chunk-2"` |

## 验证要点

- AgentId 缺失时不抛异常（与 Auth、TenantValidation 的阻断性行为形成对比）
- 流式路径正确传播所有 chunk
