# OpenAgent

OpenAgent 是基于 .NET 8、ASP.NET Core 和 Microsoft Agent Framework 的多服务 Agent 平台，后端位于 `Backend/`，Chat 前端位于 `Frontend/OpenAgent.Chat/`。

## 开发入口

| 入口 | 内容 |
|---|---|
| [开发指南](docs/overview/DevelopmentGuide.md) | 模块目录、修改边界、接口协作与测试入口 |
| [任务清单](docs/planning/TODO.md) | P0/P1/P2、剩余模块拆分、已有 PR 集成与验收 |
| [系统上下文](docs/overview/SystemContext.md) | 当前服务拓扑、功能范围与数据依赖 |
| [文档中心](docs/README.md) | 功能域、集成、数据库、API 与排障导航 |
| [部署指南](docs/deployment.md) | 启动、认证、变量、镜像更新与数据卷 |

## 构建与测试

```bash
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln
```

前端及环境测试方式见 [开发指南](docs/overview/DevelopmentGuide.md) 和 [测试分层](Backend/tests/README.md)。

## 规范与背景

- [编码规范](.agent/rules/coding-conventions.md)、[文档规范](.agent/rules/doc-standards.md)、[AI 工作流](.agent/README.md)。
- [架构决策](docs/decisions/README.md)、[历史方案](docs/archive/README.md)、[验证记录](docs/test-reports/README.md)。
