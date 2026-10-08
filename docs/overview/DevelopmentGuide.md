# 开发指南

本页是模块开发、任务分派、代码审查与验证的统一入口。服务定位见 [SystemContext](./SystemContext.md)，业务流程见 [KeyFlows](./KeyFlows.md)，编码约束见 [coding-conventions](../../.agent/rules/coding-conventions.md)。

## 开发流程

1. 从根 [README](../../README.md) 确认启动方式，再按下表确定所属模块。
2. 阅读对应 [功能域文档](../modules/README.md) 与 [集成文档](../integrations/README.md)，从已有用例追踪接口、注册与测试。
3. 任务卡写明基线、目标行为、拥有目录、共享文件、接口输入输出、前置任务、验收与回退；指定模块负责人和集成人。
4. 跨域需求先确定 Contracts 协议，再分派实现。模块负责人改本域；公共 DTO、数据库迁移、包/项目引用、Host Program 和前端 app 由集成人协调。
5. 运行相关测试与交界验证；正式文档放在 docs，按 [文档规范](../../.agent/rules/doc-standards.md) 更新所属索引。

## 项目归属

| 项目（Backend/src/ 下，统一 OpenAgent. 前缀） | 主责 |
|---|---|
| Contracts | 纯平台接口、DTO、配置、错误码；不放 SDK 类型或实现 |
| Core | 执行、会话、模型、文件、工具、能力与业务授权 |
| Engine | 配置目录、服务注册、心跳与热重载 |
| Engine.Host | HTTP/SSE、Web 中间件与最终服务组装 |
| Infrastructure | PostgreSQL、Redis 缓存与分布式锁；S3 实现目前仍在 Host |
| Hosting | 共享认证、身份、遥测与 Web 基础能力 |
| Router | 服务发现、路由、转发、限流与入口治理 |
| Runner | 沙箱执行、工作区与资源清理 |

保留八个项目和统一解决方案，功能域在项目内隔离。项目引用白名单见编码规范第 2 节及 `Backend/tests/OpenAgent.Architecture.Tests/AssemblyDependencyTests.cs`；不要凭简化分层图新增项目引用。

## Core 修改入口

下表以 `Backend/src/OpenAgent.Core/` 为根。注册文件在 `Extensions/`；公开的 `Exten/CoreServiceExtensions.cs` 仅组合各域，重复调用不会追加 Options 集合绑定。

| 模块 | 实现目录/入口 | 注册文件 |
|---|---|---|
| 执行 | Execution/AgentExecutor、AgentFactory、AgentExecutionScope、AgentRuntimeResolver | RuntimeServiceExtensions.cs |
| 模型 | ModelProviders/IAgentChatClientFactory、AgentChatClientFactory、OutputTokenLimitedChatClient | ModelProviderServiceExtensions.cs |
| 会话 | Conversation/History、Compaction、Store、Lock | ConversationServiceExtensions.cs |
| 消息转换 | Mapping/；含 ExecutionFailureDescriptor、SDK 消息、usage、工具结果转换 | 无 |
| 观测 | Observability/；模型交互捕获、记录与指标 | 随模型注册 |
| 工具机制 | Tooling/Abstractions、Discovery、Invocation；含 AgentExecutionOptions | ToolingServiceExtensions.cs |
| MCP | Capabilities/Mcp/；McpFeatureFactory、连接、资源与延迟发现 | McpServiceExtensions.cs |
| Skill | Capabilities/Skill/；SkillFeatureFactory、包、上下文与脚本 | SkillServiceExtensions.cs |
| RAG | Capabilities/Rag/；Registry、检索与 Adapter | RagServiceExtensions.cs |
| 内置能力 | Capabilities/Code、Workspace、Plan、UserProfile | BuiltinCapabilityServiceExtensions.cs |
| Runner 客户端 | Integrations/Runner/；执行与工作区协议、连接配置 | RunnerServiceExtensions.cs |
| 文件业务 | Files/Services、Sharing、Requests、Multimodal、Artifacts | FileAssetServiceExtensions.cs |
| 文件工具 | Files/Tools/ | 随文件注册 |
| 授权 | Security/；资源权限与调用检查 | SecurityServiceExtensions.cs |

Execution 不引用具体能力实现；ModelProviders 不引用会话或执行；Observability 只使用 Mapping。Files 服务不引用工具机制，工具暴露单列 Files/Tools；Skill、Workspace、Code 共用 Runner 客户端和 Files/Artifacts，不导入彼此内部实现。完整域依赖以 `CoreModuleDependencyTests.cs` 为准。

### 扩展与生命周期

- 普通工具实现本模块内部 `ICapabilitySource`，返回 CapabilityDefinition，在本域注册文件通过 TryAddEnumerable 接入；无需给 AgentFactory 增加具体能力分支。
- SDK 工具或上下文 Provider 实现内部 `IAgentFeatureFactory`，每轮返回 AgentFeature，提供工具、常驻工具、Provider、可选准备逻辑及待释放资源。MCP 资源转换与延迟阈值、Skill 临时包与 Provider 各由本域负责。
- AgentExecutionScope 在正常结束时释放历史、工具信号量与贡献资源；初始化失败逆序清理已创建资源，清理异常被记录且不替换原始错误。SDK 执行循环继续由 MAF 管理。
- 跨项目纯契约归 Contracts；Core 内部扩展接口保持 internal。公开类型保留原 namespace/assembly 身份，兼容 namespace 不改变目录归属。检查 csproj 的 InternalsVisibleTo 后再访问内部类型。

## 前端修改入口

下表以 `Frontend/OpenAgent.Chat/src/` 为根。feature 仅依赖本域与 shared，shared 不引用 feature/app，跨域协作由 app 或参数/回调完成。

| 目录 | 当前职责 |
|---|---|
| features/models、mcp、skills、rag | 各域 API、设置状态、面板与编辑/绑定弹窗；Skill Markdown 归 skills |
| features/agents/api.ts | Agent 目录与配置接口 |
| features/chat、conversations、files、auth、diagnostics 的 api.ts | 对应业务 API；旧组件和状态尚未全部迁入 |
| shared/api/http.ts、browserCrypto.ts | HTTP、错误、认证头、连接/凭证存储与浏览器加密 |
| shared/contracts/types.ts、streaming/parseSseBlock.ts | 共享 wire 类型与 SSE 解析 |
| app/useSettings.ts、SettingsDialog.vue | Agent 草稿、编辑与跨域设置组合 |
| 根 api.ts、types.ts、browserCrypto.ts、composables/useSettings.ts | 旧调用方兼容导出；新 feature 不引用这些门面 |

修改模型保存、MCP 测试、Skill 上传或 RAG 编辑时，在本域完成。兼容导出在全仓库调用方迁移后删除。未完成的迁移与前置关系见 [模块拆分后续任务](../planning/module-boundaries.md)。

## 审查与验证

- 保持 HTTP 路由、JSON 字段、错误码、SSE 顺序、schema、Redis key 和公共签名兼容；结构迁移与功能改变分别验收。Engine.Host 中间件和认证管道按现有 Program 保持，不在 Core 添加 Web 管道。
- 正确传播 CancellationToken、释放流式/连接资源，保持现有 DI 生命周期；包版本在 Directory.Packages.props 管理，不全局压制警告。检查授权/租户边界、参数化查询及凭证泄露。
- 测试使用现有 TestDoubles，命名为“方法名_场景_预期行为”，相似用例用 Theory。覆盖有意义的成功、失败与取消场景，不为路径移动重复实现镜像测试。
- Core 测试在 `Backend/tests/OpenAgent.Core.Tests/` 镜像功能域目录；跨域验证覆盖执行↔历史、MCP↔Files、Skill↔Runner↔Files、Host↔配置、Router↔Engine。默认套件不依赖 PostgreSQL/Redis 容器，环境规则见 [测试分层](../../Backend/tests/README.md)。
- 后端边界检查覆盖签名、泛型、静态调用、局部变量、异步状态机及项目引用；前端 `moduleBoundaries.test.ts` 覆盖 import、re-export、字符串 dynamic import 与 Vue script。运行时反射/动态拼接的依赖仍需审查，不能扩大白名单绕过问题。

```sh
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln --no-build
# 只检查后端边界
dotnet test Backend/tests/OpenAgent.Architecture.Tests/OpenAgent.Architecture.Tests.csproj
# 在 Frontend/OpenAgent.Chat 下执行
pnpm check
```

真实模型 E2E、Linux Bubblewrap、真实 Runner 另按 [E2E 流程](../../.agent/skills/e2e-test.md) 和 [构建测试流程](../../.agent/skills/build-and-test.md) 验证；macOS 门控跳过不计为沙箱验证。排障先确认服务 health/ready、模型或 MCP 配置与请求 TraceId，再查 [完整排查手册](../trace-troubleshoot.md)。
