# Chat 工作台

`Frontend/OpenAgent.Chat` 是面向开发、联调和平台管理的 Vue 单页工作台。浏览器可选择通过 OpenAgent Router 访问，也可在开发联调时直连单个 Engine。

```text
Browser -> OpenAgent.Router -> Engine or third-party Agent Provider
Browser ---------------------> Engine
```

## 功能

- 三栏工作区：会话列表、SSE 聊天、路由与身份上下文；
- `Auto` 意图路由和显式 Agent 选择；
- 附件上传、会话搜索、详情读取和删除；
- 失败响应下的“重试”：在当前会话重新发送上一条问题及附件，保留失败尝试和未发送草稿；连接中断但后端仍在运行时改为重连，不重复执行；
- 刷新、重新登录或再次打开运行中的会话时恢复 SSE；后端已完成时直接展示持久化结果；
- Router/Engine 连接、诊断、LLM、Agent、MCP、Skill、RAG 设置；
- 独立登录页、登录态恢复、OIDC PKCE、登出、401/403 状态和受保护页面跳转；
- 深浅主题和响应式布局。
- 每条 assistant 响应展示模型与 input/output/total Token；右侧 Inspector 展示当前会话累计。

Token 仅取服务端 Provider usage。流式生成期间等待 `done` 事件更新当前响应；历史恢复后从消息中的 `tokenUsage` 重建。Provider 未返回、请求失败/取消或旧历史缺少 usage 时显示“暂不可用”，不以正文长度估算；会话累计只按唯一 `messageId` 计算，避免重试、切换和重复加载造成二次累计。

前端请求统一由 `Frontend/OpenAgent.Chat/src/api.ts` 根据当前模式发送到 Router 或 Engine。Router 模式聚合各 Provider 的可见 Agent、支持 `Auto` 意图选择并转发聊天；Engine 模式直接使用 Engine Agent 目录，要求显式选择 Agent。Router 地址、Engine 地址与当前模式分别持久化，切换时互不覆盖。

## Router 接口

除已有的聊天、附件、Agent 目录和会话列表接口外，Router 还透传：

- `GET|DELETE /api/v1/agent/conversations/{conversationId}`；
- `GET /api/v1/agent/conversations/{conversationId}/stream` 重放当前轮已产生的事件并继续订阅；当前轮已结束时返回 `204`，客户端随后读取会话详情；
- `POST /api/v1/agent/conversations/{conversationId}/cancel` 显式停止生成并等待持久化结束；
- `GET /api/v1/agent/me`；
- `GET|POST|PUT|DELETE|PATCH /api/v1/admin/{**path}`；
- `GET /api/v1/auth/config` 与 Basic 模式下的 `POST /api/v1/auth/password/token` 由 Router 本地提供。

实现入口：

- `Backend/src/OpenAgent.Router/Endpoints/GatewayProxyHandler.cs`；
- `Backend/src/OpenAgent.Router/Extensions/RouterEndpointExtensions.cs`。

Router 是浏览器流量的信任边界。转发前会清除客户端提交的 Agent、用户、租户、会话和 Trace 内部 Header，再根据认证上下文写入可信值。

## 会话恢复

`ConversationRunRegistry` 在 Engine 内以独立 DI scope 和取消令牌执行流式轮次。浏览器刷新、断网或退出登录只断开订阅，不取消服务端生成；“停止”与删除会话显式取消执行，并在释放执行资源、持久化状态后返回。读取、重连、停止和删除均校验租户与用户归属。

独立 scope 通过 `ICurrentUserContextInitializer` 初始化可信用户快照，使 PostgreSQL 会话存储继续按原用户／租户授权，不依赖已经结束的 `HttpContext`。普通 HTTP scope 仍从当前请求惰性解析身份。浏览器 API 层将后端数字 `ConversationStatus`（0–3）归一化为状态名后再进行重连与重试判断。

前端在首次提交时记住当前会话 ID；恢复页面或登录态后重新读取该会话，即使列表尚未包含它。运行中的详情包含当前输入，重连从完整事件重放重建本轮响应，避免重复拼接已显示的片段。后端结束与重连请求同时发生时，以持久化详情为准。

活动执行与重放缓冲只保留在原 Engine 进程中；正常结束后移除，最终消息继续由现有会话存储保存。此机制覆盖页面刷新和重新登录，不提供 Engine 重启后的执行续跑；Router 重连沿用会话路由亲和性。

## 开发与安全边界

当前 Basic 认证只用于 Development 联调，它不校验真实密码，且在非 Development 环境启动失败。生产环境必须配置 OIDC/OAuth2 企业 IdP，Router 和 Engine 验证 JWT issuer、audience、签名与有效期。管理接口仍只在 Development 映射。

凭据不会持久化；token 与 OIDC 临时参数仅保存在当前标签页的 `sessionStorage`，连接模式、地址和租户配置可保存在 `localStorage`。token 绑定登录端点，退出或 401 会清理敏感会话信息；Bearer 模式的租户来自服务端 claim。直连 Engine 会绕过 Router 权限边界，只适合受控网络或开发联调。

## 本地运行

```bash
cd Frontend/OpenAgent.Chat
pnpm install
pnpm dev
```

在设置窗口分别填写 Router 地址（例如 `http://localhost:5001`）和 Engine 地址（例如 `http://localhost:5208`），选择连接模式，再填写租户并登录。

## 验证

```bash
dotnet build Backend/OpenAgent.sln -c Release --no-restore
dotnet test Backend/OpenAgent.sln -c Release --no-build

cd Frontend/OpenAgent.Chat
pnpm test
pnpm build
```

前端诊断页可检查当前 Router 或 Engine 的 Live、Ready、Agent Catalog、Identity 和 Conversations。Vite 可能报告第三方 PURE 注释和大 Chunk 提示，目前不影响构建结果。

会话重试与恢复的针对性验证：

```sh
dotnet test Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj --filter 'FullyQualifiedName~ConversationRunRegistryTests|FullyQualifiedName~ConversationRecoveryEndpointTests|FullyQualifiedName~ConversationOwnershipEndpointTests|FullyQualifiedName~ConversationRouteTests|FullyQualifiedName~HostingTests.MapAgentEndpoints|FullyQualifiedName~AgentStreamWriterTests|FullyQualifiedName~SseToolStreamingTests|FullyQualifiedName~ExecutionUserContextTests|FullyQualifiedName~AgentUserContextMiddlewareTests'
dotnet test Backend/tests/OpenAgent.Router.Tests/OpenAgent.Router.Tests.csproj --filter FullyQualifiedName~ConversationRecoveryProxyTests
cd Frontend/OpenAgent.Chat
pnpm exec vitest run src/composables/useChatStreaming.test.ts src/composables/useConversationState.test.ts src/composables/useConversationStreams.test.ts src/composables/useAuthentication.test.ts src/api.test.ts src/moduleBoundaries.test.ts
pnpm build
```

本地已覆盖订阅断开不取消生成、事件重放、显式停止与删除等待收尾、跨租户／用户拒绝访问，以及恢复读取与持久化并发。浏览器使用真实聊天组件及受控数据验证了重试保留草稿、刷新后重连、完成结果恢复和 375px 重试按钮布局；未进行真实模型推理或外部身份提供方登录的端到端验证。

在 Linux amd64 远端隔离 Docker 栈中，已用真实 OpenCode 1.19.3、PostgreSQL、Engine、Router 和 Chat 完成部署验收。浏览器覆盖失败重试且保留草稿、生成中刷新、退出后重新登录续接、退出期间已完成结果恢复与再次刷新；模型端计数证明重连场景各只触发一次模型请求，失败后手动重试共两次。API 另覆盖跨用户拒绝重连／取消、并发提交返回 409、完成后重连返回 204、显式停止先持久化 Cancelled 再返回。模型响应来自独立确定性端点，不使用外部付费模型，也未验收 OIDC 身份提供方。
