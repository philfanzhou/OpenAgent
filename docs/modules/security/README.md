# security — 安全与租户

security 域负责入口认证、独立权限契约与策略、短时委托授权票据、租户隔离和运行时资源授权。`OpenAgent.Authorization` 可由各服务层和第三方直接复用；Gateway 仅是当前的 HTTP/HMAC 适配器。

## 功能点

| 功能点 | 说明 | 文档 |
|--------|------|------|
| auth | Gateway JWT、开发 Basic 与下游签名票据 | [auth/](./auth/) |
| tenant | 租户隔离与校验 | [tenant/](./tenant/) |
| agent-id-validation | AgentId 前置校验 | [agent-id-validation/](./agent-id-validation/) |
| permission | 独立权限契约、策略替换、Agent 候选过滤与能力授权 | [permission/](./permission/) |
