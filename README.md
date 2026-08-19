# OpenAgent

OpenAgent 是基于 .NET 8 和 ASP.NET Core 的多服务 Agent 平台。生产代码位于 `Backend/`。

## 模块结构

```text
OpenAgent.Engine.Host ──> OpenAgent.Engine ──> OpenAgent.Core ──> OpenAgent.Contracts
        │                     │
        ├──> OpenAgent.Infrastructure ──> OpenAgent.Core
        ├──> OpenAgent.Hosting ──> OpenAgent.Contracts
        └──> OpenAgent.Router ──> OpenAgent.Core
```

| 模块 | 职责 |
|------|------|
| `OpenAgent.Contracts` | 跨模块接口、配置与 DTO（纯契约层） |
| `OpenAgent.Core` | 执行引擎、能力（MCP/RAG/Skill）、会话抽象、安全 |
| `OpenAgent.Engine` | Agent 执行服务、注册表、健康检查、配置热重载 |
| `OpenAgent.Engine.Host` | ASP.NET Core 宿主（端点、中间件、流式传输、文件资产接入） |
| `OpenAgent.Hosting` | 共享宿主、认证、Redis 与 OpenTelemetry 注册 |
| `OpenAgent.Infrastructure` | PostgreSQL+EF Core 持久化、Redis 写穿缓存、分布式锁 |
| `OpenAgent.Router` | 网关服务（路由、意图识别、限流、租户隔离） |

## 构建与测试

```bash
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln
```

## Docker 本地全栈

```bash
docker compose up --build
```

启动后访问 `http://localhost:8080`。工作台默认使用 Router `http://localhost:5001`、直连
Engine `http://localhost:5208`、租户 `development`；Compose 同时启动 PostgreSQL、Redis 和 MinIO。
服务间使用 Docker DNS 地址（例如 Router -> `http://engine:5208`），浏览器仍使用上述 localhost
地址。端口可通过 `OPENAGENT_*_PORT` 环境变量覆盖。Compose 不包含任何模型凭据或已发布 Agent；首次
执行聊天前，请先在登录页使用任意非空账号密码建立 Development 身份，再在工作台设置中创建 LLM
Provider 并绑定 Agent。该兼容登录不校验真实密码，仅适合本地联调，不应直接暴露到公网。

## 文档

- [文档中心](docs/README.md) — 总览、模块、集成、数据库、架构决策
- [.agent/](.agent/README.md) — AI 工具资源（技能、规则、指南）

## 安全

生产认证使用可配置的 OIDC/OAuth2 身份提供方与 JWT Bearer 校验，验证 issuer、audience、签名和有效期。
Basic 兼容登录严格限制在 Development；它只解析凭据，不查询用户目录，也不校验真实密码。
认证只负责建立身份，角色、Agent ACL、能力权限与租户授权由独立的服务端授权层判断。
详见 [安全设计文档](docs/modules/security/README.md)。
