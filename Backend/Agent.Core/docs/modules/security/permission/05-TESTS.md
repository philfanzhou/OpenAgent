# PermissionEvaluator — 测试覆盖

## 测试文件

PermissionEvaluator 的测试通过 Auth 中间件测试间接覆盖：

`test/OpenAgent.Core.Tests/Middleware/AuthTests.cs`

## 间接测试覆盖

AuthTests 中的 `FakePermissionEvaluator` 模拟了 `IPermissionEvaluator` 的行为：

| Auth 测试用例 | 覆盖的 PermissionEvaluator 行为 |
|--------------|-------------------------------|
| `InvokeAsync_passes_through_when_authenticated` | `IsAuthenticatedAsync` 返回 `true` 时，Auth 通过 |
| `InvokeAsync_throws_AgentException_when_not_authenticated` | `IsAuthenticatedAsync` 返回 `false` 时，Auth 抛异常 |
| `InvokeStreamAsync_throws_AgentException_when_not_authenticated` | 流式路径下 `IsAuthenticatedAsync` 返回 `false` |

## FakePermissionEvaluator

测试内嵌的 `IPermissionEvaluator` 桩实现：

```csharp
private sealed class FakePermissionEvaluator : IPermissionEvaluator
{
    private readonly bool _isAuthenticated;
    public FakePermissionEvaluator(bool isAuthenticated) => _isAuthenticated = isAuthenticated;
    public Task<bool> IsAuthenticatedAsync(IAgentUserContext userContext, CancellationToken cancellationToken = default)
        => Task.FromResult(_isAuthenticated);
}
```

## 验证要点

- `IsAuthenticatedAsync` 返回 `bool`，Auth 中间件根据结果决定是否抛出异常
- `PermissionEvaluator` 默认实现检查 `userContext.IsAuthenticated` 属性

## 资源授权测试

| 文件 | 覆盖 |
|---|---|
| `Security/AgentAuthorizationGateTests.cs` | 六种资源维度拒绝时统一返回 `PermissionDenied` |
| `Security/ExecutionAuthorizationTests.cs` | Agent 或 Model 被拒绝时不会调用模型引擎 |

Tool/Function/MCP/Skill 的生产路径还需在第二阶段 MAF function middleware 接管工具循环时增加端到端回归。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
