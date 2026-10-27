# OpenAgent

OpenAgent 是基于 .NET 10 与 ASP.NET Core 的多服务 Agent 平台：后端服务位于 `Backend/`，Chat 工作台前端位于 `Frontend/OpenAgent.Chat/`。平台通过独立的单包 ACP SDK 驱动 [OpenCode](docs/integrations/opencode-runtime.md) 执行聊天推理，围绕会话提供 Skill、MCP、RAG、代码执行等能力集成，并配套认证、多租户、网关路由与容器化部署方案。

## 功能特性

**执行与会话**

- 执行管线与统一错误处理：请求验证、追踪审计、错误分类与错误码（[Execution](docs/modules/execution.md)）
- HTTP/SSE 流式推理输出，OpenCode 子进程逐轮工具迭代
- 会话与消息持久化（写穿透）、交互日志、长上下文压缩与压缩恢复（[Conversation](docs/modules/conversation.md)）
- Agent / 模型配置存于数据库，支持配置热重载（[Engine](docs/modules/engine.md)）

**能力集成**

- Skill 目录与 Skill 包物化、内置工具与调用授权（[Capabilities](docs/modules/capabilities.md)）
- MCP Server 工具发现、调用与资源读取；外部 server 经 ACP 透传给 harness 原生连接（[MCP](docs/integrations/mcp-server.md)）
- RAG 检索增强，对接 Qdrant / RagFlow（[RAG](docs/integrations/rag-service.md)）
- CodeAct 代码执行：Runner 在 Linux 沙箱（Bubblewrap）中运行代码并管理会话工作区（[Runner](docs/integrations/code-runner.md)）

**安全与多租户**

- 认证：生产 OIDC（Authorization Code + PKCE）/ JWT Bearer，开发态 Basic 与 API Key（[Security](docs/modules/security.md)）
- 权限评估、租户校验与模型授权

**网关与运维**

- Router：意图选路、Redis 服务发现索引、故障回退、限流降级与就绪探测（[Router](docs/modules/router.md)）
- 健康检查（Live / Ready）、优雅停机、服务注册与心跳
- 可观测性：日志、追踪与排障指引（[Observability](docs/overview/Observability.md)）
- 容器化部署与并行预览环境（[部署指南](docs/operations/deployment.md)）

**Chat 工作台**

- Vue 3 单页工作台：会话管理、流式渲染、健康检查页，支持 Router 接入或直连 Engine 联调（[Chat Workspace](docs/modules/chat-workspace.md)）

## 系统架构

```text
Chat（浏览器）→ Router（HTTP 网关）→ Engine.Host（HTTP/SSE 宿主）
                                      ├─ Application：执行协调、会话、模型授权与工具适配
                                      ├─ Capabilities：文件、RAG、内置工具、调用授权与 Runner 客户端
                                      ├─ Runtime：ACP SDK → OpenCode 子进程 → LLM API
                                      ├─ Engine：配置、注册、心跳与热重载
                                      │    └─ Hosting：共享认证、身份与遥测（Router 也使用）
                                      └─ Framework：PostgreSQL、Redis 存储实现
OpenCode → 本轮 MCP 桥 → Application 平台工具 / 外部 MCP
Application → Capabilities → RAG（外部服务）
Application → Capabilities → Runner（HTTP）→ Linux 沙箱与会话工作区
Contracts：跨项目的纯接口、配置、DTO 与无 SDK 的能力定义
```

后端项目按功能与依赖边界拆分，项目引用白名单由架构测试（`Backend/tests/OpenAgent.Architecture.Tests/`）强制检查：

| 项目 | 职责 |
|---|---|
| `OpenAgent.Contracts` | 共享接口、配置、DTO 与不含 SDK 的能力定义 |
| `OpenAgent.Application` | 执行协调、会话、模型授权、工具适配与组合入口 |
| `OpenAgent.Capabilities` | 文件、RAG、Skill 目录、内置工具、调用授权及 Runner 客户端 |
| `OpenAgent.Engine` | 配置、Redis 注册、热重载；Hosting/ 内为共享认证与遥测 |
| `OpenAgent.Engine.Host` | HTTP/SSE、管理端点与最终组合根 |
| `OpenAgent.Runtime` | ACP/OpenCode 平台驱动、事件与每轮 MCP 桥（不打包） |
| `OpenAgent.Runtime.Acp` | 独立单包 ACP SDK，不引用平台模块 |
| `OpenAgent.Framework` | EF Core、PostgreSQL、Redis 存储实现 |
| `OpenAgent.Router` | 路由、转发、限流与入口认证 |
| `OpenAgent.Runner` | 沙箱与工作区 |

各模块使用同名 `.Tests` 项目。调用边界、数据主责与功能范围的权威说明见 [系统上下文](docs/overview/SystemContext.md)。

## 技术栈

| 层 | 技术 |
|---|---|
| 后端 | .NET 10（SDK `10.0.100`）、ASP.NET Core、EF Core |
| 前端 | Vue 3、TypeScript、Vite、Element Plus、pnpm |
| 数据库 | PostgreSQL（会话、消息、配置与文件资产元数据） |
| 缓存与协调 | Redis（缓存、服务发现、分布式协调与配置通知） |
| 对象存储 | S3 兼容存储，本地使用 MinIO（文件字节与 Skill 包） |
| 推理执行 | OpenCode（ACP 协议）、Microsoft.Extensions.AI |
| 身份认证 | Keycloak / 企业 IdP（OIDC + PKCE、JWT） |
| 部署 | Docker Compose、Nginx |

## 仓库结构

```text
Backend/
├── src/                后端源码（见上方项目表）
├── tests/              各模块测试 + 架构测试
└── OpenAgent.sln
Frontend/OpenAgent.Chat/  Vue 3 Chat 工作台
docs/                     功能域、集成、数据库、API 与排障文档
deploy/
├── infrastructure/       PostgreSQL、Redis、MinIO、Keycloak 编排
├── openagent/            Engine、Runner、Router、Chat、Nginx 编排
└── deployment.env.example 环境变量模板
docker/nginx/             Nginx 配置
scripts/                  构建、部署、预览与沙箱测试脚本
.agent/                   AI 技能、编码与文档规范
```

## 快速开始

### 环境要求

- .NET 10 SDK（版本由 `global.json` 固定）
- Node.js + pnpm（前端）
- Docker 与 Docker Compose（完整部署）

### 后端构建与测试

```bash
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln
```

### 前端本地开发

```bash
cd Frontend/OpenAgent.Chat
pnpm install
pnpm dev
```

登录页默认连接 Router `http://localhost:5001`。Development 模式可填写任意非空账号密码建立 Basic 联调身份；Production 使用企业 IdP 的 OIDC，详见 [Chat README](Frontend/OpenAgent.Chat/README.md)。

### 本地部署

使用 Docker Compose 启动基础设施（PostgreSQL、Redis、MinIO、Keycloak）与平台服务（Engine、Runner、Router、Chat、Nginx）：

```bash
cp deploy/deployment.env.example deploy/deployment.env  # 按需修改
# 按 docs/operations/deployment.md 启动两个 compose
```

启动步骤、认证配置、环境变量、数据卷与镜像更新见 [部署指南](docs/operations/deployment.md)；本地预览脚本见 [本地预览](docs/operations/local-preview.md)。

## 测试

- 后端：`dotnet test Backend/OpenAgent.sln`，测试分层见 [Backend/tests/README.md](Backend/tests/README.md)
- 前端：`pnpm test`（Vitest）与 `pnpm build`（含类型检查）
- 架构门禁：模块引用白名单与前端 import 依赖均有测试检查，不应通过扩大白名单绕过

## 文档导航

| 入口 | 内容 |
|---|---|
| [开发指南](docs/overview/DevelopmentGuide.md) | 模块目录、修改边界、接口协作与测试入口 |
| [任务清单](docs/planning/TODO.md) | P0/P1/P2、剩余模块拆分、已有 PR 集成与验收 |
| [系统上下文](docs/overview/SystemContext.md) | 当前服务拓扑、功能范围与数据依赖 |
| [功能域索引](docs/modules/README.md) | execution、conversation、capabilities、security、engine、router、chat-workspace |
| [集成索引](docs/integrations/README.md) | LLM、Redis、PostgreSQL、MCP、RAG、Keycloak 等外部依赖 |
| [文档中心](docs/README.md) | 功能域、集成、数据库、API 与排障导航 |
| [部署指南](docs/operations/deployment.md) | 启动、认证、变量、镜像更新与数据卷 |

## 参与开发

- 编码规范：[coding-conventions.md](.agent/rules/coding-conventions.md)（机器可检查部分下沉到 `.editorconfig` 与 `Directory.Build.props`）
- 文档规范：[doc-standards.md](.agent/rules/doc-standards.md)；正式文档统一放 `docs/`，源码目录不散放 README
- AI 辅助开发工作流：[.agent/README.md](.agent/README.md)
- 包版本统一在 `Backend/Directory.Packages.props` 管理，`.csproj` 中不写 `Version`

## 许可证

[MIT](LICENSE)
