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

Router 是浏览器流量的信任边界。转发前会清除客户端提交的 Agent、用户、租户、会话和 Trace 内部 Header，再根据认证上下文写入可信值。

## 开发与安全边界

当前 Basic 认证只用于 Development 联调，它不校验真实密码，且在非 Development 环境启动失败。生产环境必须配置 OIDC/OAuth2 企业 IdP，Router 和 Engine 验证 JWT issuer、audience、签名与有效期。管理接口仍只在 Development 映射。

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
