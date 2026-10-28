# 系统上下文

OpenAgent 是基于 .NET 8 与 ASP.NET Core 的多服务 Agent 平台。当前代码模块和修改入口见 [开发指南](DevelopmentGuide.md)，运行时细节见 [Design](Design.md)，未完成能力见 [任务清单](../planning/TODO.md)。

## 当前部署与调用边界

```text
Chat（浏览器）→ Router（HTTP 网关）→ Engine.Host（HTTP/SSE 宿主）
                                      ├─ Core：MAF 执行、能力、会话与业务授权
                                      ├─ Engine：配置、注册、心跳与热重载
                                      ├─ Infrastructure：PostgreSQL、Redis 实现
                                      └─ Hosting：共享认证、身份与遥测
Core → LLM / MCP / RAG（外部服务）
Core → Runner（HTTP）→ Linux 沙箱与会话工作区
Contracts：跨项目的纯接口、配置与 DTO
```

该图表示运行时调用与宿主组装，不是项目引用白名单。Router 通过 HTTP 转发到 Engine.Host，不引用 Core；Core 是类库，不独立部署、不暴露 HTTP、不管理宿主生命周期。具体项目引用以 `.csproj` 和架构测试为准。

## 外部依赖与数据主责

| 依赖 | 用途与主责 |
|---|---|
| MAF / Microsoft.Extensions.AI / Provider SDK | Agent 执行、模型适配、函数调用与上下文 Provider |
| LLM API | 模型推理、流式输出与函数调用 |
| MCP Server | 工具发现、调用与资源读取 |
| RAG 服务 | Qdrant/RagFlow 检索增强 |
| PostgreSQL | 会话、消息、配置与文件资产元数据的持久化事实源 |
| S3 兼容对象存储 | 文件原始字节和 Skill 包对象；本地采用 MinIO |
| Redis | 缓存、分布式协调、服务发现与配置通知 |
| 身份提供方 | 生产 HTTP 身份认证；权限执行边界见安全文档 |

完整数据边界见 [DataOwnership](DataOwnership.md)，配置和失败语义见 [集成索引](../integrations/README.md)。

## 功能范围

| 原需求编号 | 范围 | 详细文档 |
|---|---|---|
| R-01 | MAF 推理与数据库中的 Agent/模型配置 | [Engine](../modules/engine/README.md) |
| R-02 | 请求管线、验证、追踪与审计 | [Pipeline](../modules/execution/pipeline/README.md) |
| R-03～R-06 | Skill、MCP、RAG 与工具调用 | [能力索引](../modules/capabilities/README.md) |
| R-07 | 会话持久化与独立文件资产 | [会话存储](../modules/conversation/store/README.md)、[文件资产](../integrations/file-assets.md) |
| R-08 | 流式推理输出 | [Streaming](../modules/execution/streaming/README.md) |
| R-09 | 错误分类、传播与错误码 | [错误处理](../modules/execution/errors/README.md) |
| R-10 | 认证、租户与能力授权 | [安全索引](../modules/security/README.md) |

功能范围不表示全部生产保障已完成；异常重放、压缩恢复、MCP 认证与统一授权的剩余范围见任务清单。HTTP 端点归 Engine.Host/Router，前端归独立 Chat 项目，配置管理归 Engine 及 Host 管理入口。

## Workflow 兼容入口

当前仓库没有 Agent.Workflow 项目、程序集、容器或可部署端点。Router 仅保留 `workflow` 意图和 `RouterSettings:Routing:WorkflowEndpoint` 配置契约，Development 地址是占位配置。

未部署 Workflow 时不应向该入口发送请求，生产推理应转发到已注册的 Engine。接入实现前需补齐鉴权、健康检查、发现、超时降级和集成测试，任务见 F06；占位入口不作为服务可用性证据。
