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
- Router/Engine 连接、诊断、LLM、Agent、MCP、Skill、RAG 设置；
- 独立登录页、登录态恢复、OIDC PKCE、登出、401/403 状态和受保护页面跳转；
- 深浅主题和响应式布局。
- 每条 assistant 响应展示模型与 input/output/total Token；右侧 Inspector 展示当前会话累计。

Token 仅取服务端 Provider usage。流式生成期间等待 `done` 事件更新当前响应；历史恢复后从消息中的 `tokenUsage` 重建。Provider 未返回、请求失败/取消或旧历史缺少 usage 时显示“暂不可用”，不以正文长度估算；会话累计只按唯一 `messageId` 计算，避免重试、切换和重复加载造成二次累计。

前端请求统一由 `Frontend/OpenAgent.Chat/src/api.ts` 根据当前模式发送到 Router 或 Engine。Router 模式聚合各 Provider 的可见 Agent、支持 `Auto` 意图选择并转发聊天；Engine 模式直接使用 Engine Agent 目录，要求显式选择 Agent。Router 地址、Engine 地址与当前模式分别持久化，切换时互不覆盖。

## Router 接口

除已有的聊天、附件、Agent 目录和会话列表接口外，Router 还透传：

- `GET|DELETE /api/v1/agent/conversations/{conversationId}`；
- `GET /api/v1/agent/me`；
- `GET|POST|PUT|DELETE|PATCH /api/v1/admin/{**path}`；
- `GET /api/v1/auth/config` 与 Development-only `POST /api/v1/auth/password/token` 由 Router 本地提供。

实现入口：

- `Backend/src/OpenAgent.Router/Endpoints/GatewayProxyHandler.cs`；
- `Backend/src/OpenAgent.Router/Extensions/RouterEndpointExtensions.cs`。

Router 是浏览器流量的第一层信任边界。当前 YARP 仍保留客户端提交的 `Authorization`、用户、租户和
Agent Header；Engine 必须继续只信任已验证 Claims。清洗 Header、建立 Router workload 身份并签发
受众受限委托的目标方案见
[`ADR-0004`](../../decisions/0004-Authentication-Authorization-Trust-Boundary.md)。

## 开发与安全边界

当前 Basic 认证只用于 Development 联调，只校验仓库内置的固定账号，且在非 Development 环境启动失败。
生产环境必须配置 OIDC/OAuth2 企业 IdP，Router 和 Engine 验证 JWT issuer、audience、签名与有效期。
管理接口仍只在 Development 映射。

凭据不会持久化；token 与 OIDC 临时参数仅保存在当前标签页的 `sessionStorage`，连接模式、地址和租户配置可保存在 `localStorage`。token 绑定登录端点，退出或 401 会清理敏感会话信息；Bearer 模式的租户来自服务端 claim。直连 Engine 会绕过 Router 权限边界，只适合受控网络或开发联调。

## 本地运行

```bash
cd Frontend/OpenAgent.Chat
pnpm install
pnpm dev
```

在设置窗口分别填写 Router 地址（例如 `http://localhost:5001`）和 Engine 地址（例如 `http://localhost:5208`），选择连接模式，再填写租户并登录开发账号。

## 验证

```bash
dotnet build Backend/OpenAgent.sln -c Release --no-restore
dotnet test Backend/OpenAgent.sln -c Release --no-build

cd Frontend/OpenAgent.Chat
pnpm test
pnpm build
```

前端诊断页可检查当前 Router 或 Engine 的 Live、Ready、Agent Catalog、Identity 和 Conversations。Vite 可能报告第三方 PURE 注释和大 Chunk 提示，目前不影响构建结果。
