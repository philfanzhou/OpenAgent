# security — 安全与租户

security 域负责 Gateway 入口认证、短时授权票据、租户隔离和运行时资源授权。

## 功能点

| 功能点 | 说明 | 文档 |
|--------|------|------|
| auth | Gateway JWT、开发 Basic 与下游签名票据 | [auth/](./auth/) |
| tenant | 租户隔离与校验 | [tenant/](./tenant/) |
| agent-id-validation | AgentId 前置校验 | [agent-id-validation/](./agent-id-validation/) |
| permission | Gateway 统一策略、Agent 候选过滤与能力授权 | [permission/](./permission/) |
