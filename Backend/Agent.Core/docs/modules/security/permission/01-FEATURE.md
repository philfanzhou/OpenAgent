# PermissionEvaluator — 权限评估

## 功能定位

权限模块包含两层：`IPermissionEvaluator` 判断用户是否已认证，`IAgentAuthorizationService` 对 Agent、Model、Tool、Function、MCP、Skill 六类运行时资源授权。

## 源码

- `src/Core/Security/IPermissionEvaluator.cs` — 接口定义
- `src/Core/Security/PermissionEvaluator.cs` — 默认实现
- `src/Core/Security/IAgentAuthorizationService.cs` — 细粒度授权扩展接口
- `src/Core/Security/AgentAuthorizationGate.cs` — 统一拒绝语义
- `Agent.Contracts/Security/AgentAuthorization.cs` — 资源维度与授权请求契约

## 核心行为

- `IPermissionEvaluator` 是 public 接口，定义 `IsAuthenticatedAsync` 方法
- 默认实现 `PermissionEvaluator`（internal）仅检查 `IAgentUserContext.IsAuthenticated` 属性
- Auth 中间件依赖此接口进行认证判断
- 未认证时记录 Debug 日志并返回 `false`
- 默认 `AllowAllAgentAuthorizationService` 保持旧配置兼容；生产部署可用 DI 替换
- Agent/Model 在执行初始化阶段校验，Tool/Function/MCP/Skill 在发现和执行阶段校验

## 设计意图

认证与资源授权分离。RBAC/ABAC、租户策略或外部 PDP 应实现 `IAgentAuthorizationService`，无需把策略硬编码进 MAF 或工具适配器。

## 相关文档

- [02-SPEC](./02-SPEC.md) — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范
