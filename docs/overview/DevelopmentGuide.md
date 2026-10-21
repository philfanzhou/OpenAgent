# 开发指南

本页是模块开发、任务分派、代码审查与验证的统一入口。服务定位见 [SystemContext](./SystemContext.md)，业务流程见 [KeyFlows](./KeyFlows.md)，编码约束见 [coding-conventions](../../.agent/rules/coding-conventions.md)。

## 开发流程

1. 从 [任务清单](../planning/TODO.md) 领取任务，按下表确定所属模块；启动方式见 [部署指南](../operations/deployment.md)。
2. 阅读对应 [功能域文档](../modules/README.md) 与 [集成文档](../integrations/README.md)，从已有用例追踪接口、注册与测试。
3. 任务卡写明基线、目标行为、拥有目录、共享文件、接口输入输出、前置任务、验收与回退；指定模块负责人和集成人。
4. 跨域需求先确定 Contracts 协议，再分派实现。模块负责人改本域；公共 DTO、数据库迁移、包/项目引用、Host Program 和前端 app 由集成人协调。
5. 运行相关测试与交界验证；正式文档放在 docs，按 [文档规范](../../.agent/rules/doc-standards.md) 更新所属索引。

## 项目归属

当前分支在目录重组基线上接入 OpenCode ACP 执行，细节见 [OpenCode 运行时](../integrations/opencode-runtime.md)。Worker、RunGrant 与生产发布仍是独立待办。历史提案中的目标能力不代表当前实现状态。

| 项目（Backend/src/ 下，统一 OpenAgent. 前缀） | 主责 |
|---|---|
| Contracts | 平台接口、DTO、配置、错误码、CapabilityDefinition、ICapabilitySource 与目录接口 |
| Application | 执行协调、会话、模型授权、Mapping、Observability、工具机制与 MCP/Skill 适配 |
| Capabilities | Files、Rag、Skills 目录、Code/Workspace/Plan/UserProfile、Security、Runner 客户端 |
| Engine | 配置目录、服务注册、心跳与热重载；Hosting/ 合并原认证、身份、遥测与 Web 基础能力 |
| Engine.Host | HTTP/SSE、Web 中间件、对象存储组装与最终服务组合 |
| Runtime | ACP/OpenCode 平台适配、进程、事件映射与每轮 MCP 桥（不发布 NuGet） |
| Runtime.Acp | 独立单包协议 SDK，不引用任何平台模块 |
| Framework | 原 Infrastructure 的 PostgreSQL、Redis 缓存与分布式锁 |
| Router | 服务发现、路由、转发、限流与入口治理 |
| Runner | 原有沙箱执行、工作区与资源清理 |

引用边界：Application → Contracts/Capabilities；Capabilities、Engine、Framework、Runner → Contracts；Router → Engine/Contracts；Runtime → Contracts/Runtime.Acp；Runtime.Acp 不引用平台；Engine.Host 组装 Application/Engine/Framework/Contracts/Runtime（能力注册由 Application 组合）。Engine 不引用执行代码，Router 仅使用 Engine/Hosting。规则和架构测试必须同步，不通过兼容项目或重复实现保留旧边界。

## Application 与 Capabilities 修改入口

公开组合入口是 `Backend/src/OpenAgent.Application/Exten/ApplicationServiceExtensions.cs`；`AddAgentApplication` 仍保证重复调用不追加 Options 集合绑定。各域独立维护注册文件。

| 模块 | 所属项目与实现目录 | 注册文件 |
|---|---|---|
| 执行 | Application/Execution/ | RuntimeServiceExtensions.cs |
| 模型、转换、观测 | Application/{ModelProviders,Mapping,Observability}/ | ModelProviderServiceExtensions.cs |
| 会话 | Application/Conversation/{History,Compaction,Store,Lock}/ | ConversationServiceExtensions.cs |
| SDK 工具机制 | Application/Tooling/{Abstractions,Discovery,Invocation}/ | ToolingServiceExtensions.cs |
| MCP 适配 | Application/Capabilities/Mcp/ | McpServiceExtensions.cs |
| Skill 适配与包解析 | Application/Capabilities/Skill/ | SkillServiceExtensions.cs |
| Skill 目录 | ISkillCatalog 在 Contracts/Capabilities/；Engine 实现在 Engine/Config/SkillCatalogStore | 随 Skill 组合注册 |
| RAG | Capabilities/Rag/ | RagServiceExtensions.cs |
| 内置能力 | Capabilities/{Code,Workspace,Plan,UserProfile}/ | BuiltinCapabilityServiceExtensions.cs |
| Runner 客户端 | Capabilities/Integrations/Runner/ | RunnerServiceExtensions.cs |
| 文件 | Capabilities/Files/{Services,Sharing,Requests,Multimodal,Artifacts,Tools}/ | FileAssetServiceExtensions.cs |
| 调用授权 | Capabilities/Security/ | SecurityServiceExtensions.cs |

Capabilities 不依赖 MCP/模型 SDK 或 Application。Application 的 Execution 不引用具体能力实现，通过 Contracts 的能力接口和内部 SDK Feature 接入。Files 服务与 Files/Tools 分离；SDK Skill、Workspace、Code 共用 Runner 客户端和 Files/Artifacts。完整模块依赖由 `ApplicationModuleDependencyTests.cs` 检查。

### 扩展与生命周期

- 普通工具实现 Contracts 的 `ICapabilitySource`，返回 CapabilityDefinition，在本域注册文件通过 TryAddEnumerable 接入；无需给 AgentFactory 增加具体能力分支。
- SDK 工具或上下文 Provider 实现内部 `IAgentFeatureFactory`，每轮返回 AgentFeature，提供工具、常驻工具、Provider、可选准备逻辑及待释放资源。MCP 资源转换与延迟阈值、Skill 临时包与 Provider 各由本域负责。
- RuntimePreparationScope 在正常结束、失败或取消时释放历史、工具信号量与贡献资源；清理异常不替换原始执行错误。推理/工具迭代由 OpenCode 管理，平台只提供已授权工具回调。历史与 Skill 均由平台自持实现，不执行聊天推理。
- 跨项目纯契约归 Contracts；SDK 扩展接口在 Application 内保持 internal；跨模块能力接口归 Contracts。源码 namespace/assembly 随所属项目更新，HTTP/JSON、数据库 schema 与迁移 ID 不变。检查 csproj 的 InternalsVisibleTo 后再访问内部类型。

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

修改模型保存、MCP 测试、Skill 上传或 RAG 编辑时，在本域完成。兼容导出在全仓库调用方迁移后删除。未完成的迁移与前置关系见 [任务清单](../planning/TODO.md)。

## 审查与验证

- 保持 HTTP 路由、JSON 字段、错误码、SSE 顺序、schema、Redis key 兼容；结构迁移与功能改变分别验收。Engine.Host 中间件和认证管道按现有 Program 保持，不在 Application 添加 Web 管道。
- 正确传播 CancellationToken、释放流式/连接资源，保持现有 DI 生命周期；包版本在 Directory.Packages.props 管理，不全局压制警告。检查授权/租户边界、参数化查询及凭证泄露。
- 测试使用现有 TestDoubles，命名为“方法名_场景_预期行为”，相似用例用 Theory。覆盖有意义的成功、失败与取消场景，不为路径移动重复实现镜像测试。
- Application/Capabilities 测试在同名 `.Tests` 项目中镜像功能域目录；跨域验证覆盖执行↔历史、MCP↔Files、Skill↔Runner↔Files、Host↔配置、Router↔Engine。默认套件不依赖 PostgreSQL/Redis 容器，环境规则见 [测试分层](../../Backend/tests/README.md)。
- 合并后的 Engine.Tests/Hosting 包含进程内宿主测试，不是外部部署 E2E。隔离认证 fixture 的配置来源，避免 Engine.Host 的 appsettings.json 污染；清空 `Configuration.Sources` 后，先添加内存配置源，再调用 `UseUrls`。
- 后端边界检查覆盖签名、泛型、静态调用、局部变量、异步状态机及项目引用；前端 `moduleBoundaries.test.ts` 覆盖 import、re-export、字符串 dynamic import 与 Vue script。运行时反射/动态拼接的依赖仍需审查，不能扩大白名单绕过问题。

```sh
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln --no-build
# 只检查后端边界
dotnet test Backend/tests/OpenAgent.Architecture.Tests/OpenAgent.Architecture.Tests.csproj
# 在 Frontend/OpenAgent.Chat 下执行
pnpm check
```

真实模型 E2E、Linux Bubblewrap、真实 Runner 另按 [E2E 流程](../../.agent/skills/e2e-test.md) 和 [构建测试流程](../../.agent/skills/build-and-test.md) 验证；macOS 门控跳过不计为沙箱验证。排障先确认服务 health/ready、模型或 MCP 配置与请求 TraceId，再查 [完整排查手册](../operations/trace-troubleshoot.md)。
