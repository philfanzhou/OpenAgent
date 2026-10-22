# Design — OpenAgent

## 请求主链

本分支默认通过 OpenCode ACP 执行聊天推理。SDK、平台驱动、应用协调各自承担明确职责，详细配置见 [OpenCode 运行时](../integrations/opencode-runtime.md)。

```text
Engine.Host HTTP/SSE + 身份/入口治理
  → Application.AgentExecutor → RuntimeAgentExecutor
      → 授权模型快照 / 文件校验 / PlatformChatHistory
      → RuntimePreparation：能力、MCP、Skill → RuntimeTool 回调
      → Contracts.IAgentRuntimeDriver
          → Runtime.OpenCodeRuntime
              → Runtime.Acp SDK → stdio → OpenCode → 选定的 LLM API
              ← session/update → AgentStreamEvent
              ← OpenCode → 每轮 loopback MCP 桥 → 平台工具
      → 保存会话成功 → usage/done
```

OpenCode 管理模型请求、流式更新和工具迭代。平台保留身份、模型授权、会话一致性、文件资产、能力授权、工具预算与持久化。取消等待 ACP 原 prompt 确认，进程退出由 SDK 租约兜底回收。

## 项目与契约边界

| 层 | 职责与引用 |
|---|---|
| Runtime.Acp | 单 NuGet SDK；Protocol/Transport/RPC/Client/Agent/Processes；不引用平台项目 |
| Contracts/Runtime | IAgentRuntimeDriver、RuntimeInvocation、RuntimeTool；不暴露 ACP/MAF 类型 |
| Runtime | 不打包的平台适配类库；仅引用 Contracts 与 Runtime.Acp，使用官方 MCP SDK 承接工具调用 |
| Application | 只通过平台契约运行驱动；组合历史、能力与 Skill，不引用 Runtime/Runtime.Acp |
| Engine.Host | 注册 AddAgentApplication 与 AddOpenCodeRuntime，组装存储、认证及服务 |

项目引用由 AssemblyDependencyTests 验证。各域所有者与修改入口见 [开发指南](DevelopmentGuide.md)。

## 会话一致性与兼容范围

PostgreSQL 继续是会话与文件资产的持久化事实源；Redis 是缓存与协调设施，S3 兼容存储只保存原始字节。既有会话锁和 expectedVersion 保持有效。

每轮创建新 OpenCode 进程/会话，平台历史作为有角色标识的 JSON 上下文传入；不把 ACP session ID 当作持久化会话主键，不依赖跨进程 session/load。取消或失败保存已输出的正文、reasoning 与工具记录。

Microsoft.Extensions.AI 仍作为工具（`AIFunction`）与摘要客户端的底层抽象保留；历史、Skill 与压缩均为平台自持实现。MAF 包已随 PR #234 移除，生产入口没有第二条推理路径。Worker、分布式 RunGrant 是后续任务，不能以本次接入代替验收。

## DI 入口

```csharp
services.AddAgentApplication(configuration);
services.AddOpenCodeRuntime(configuration);
services.AddOpenAgentFramework(configuration);
services.AddFileAssetObjectStorage(configuration);
services.AddAgentEngine(configuration);
```

SDK 使用者不需要引用 OpenAgent.Runtime 或 Application。技术栈为 .NET 10、ACP v1、OpenCode（不固定版本）、官方 MCP C# SDK、PostgreSQL/Redis/S3 与 xUnit。
