# Chat 工作台

`Frontend/OpenAgent.Chat` 是面向开发、联调和平台管理的 Vue 单页工作台。浏览器只配置 OpenAgent Router 地址，不直接感知 Engine 实例。

```text
Browser -> OpenAgent.Router -> Engine or third-party Agent Provider
```

## 功能

- 三栏工作区：会话列表、SSE 聊天、路由与身份上下文；
- `Auto` 意图路由和显式 Agent 选择；
- 附件上传、会话搜索、详情读取和删除；
- Gateway、诊断、LLM、Agent、MCP、Skill、RAG 设置；
- 深浅主题和响应式布局。

前端请求统一由 `Frontend/OpenAgent.Chat/src/api.ts` 发送到 Router。Router 聚合各 Provider 的可见 Agent、选择目标并转发聊天，不要求浏览器了解 Provider 或 Engine 地址。

## Gateway 接口

除已有的聊天、附件、Agent 目录和会话列表接口外，Router 还透传：

- `GET|DELETE /api/v1/agent/conversations/{conversationId}`；
- `GET /api/v1/agent/me`；
- `GET|POST|PUT|DELETE|PATCH /api/v1/admin/{**path}`；
- `GET|POST /api/v1/auth/{**path}`。

实现入口：

- `Backend/src/OpenAgent.Router/Endpoints/GatewayProxyHandler.cs`；
- `Backend/src/OpenAgent.Router/Extensions/RouterEndpointExtensions.cs`。

Router 是浏览器流量的信任边界。转发前会清除客户端提交的 Agent、用户、租户、会话和 Trace 内部 Header，再根据认证上下文写入可信值。

## 开发与安全边界

当前 Basic 认证只用于本地联调，它不校验真实密码。Router 和 Engine 仅在 `Development` 环境映射 `/api/v1/auth/**` 与 `/api/v1/admin/**`；生产环境必须接入正式身份提供方和统一权限策略。

开发凭据保存在 `sessionStorage`，Gateway 和租户配置保存在 `localStorage`。不要在共享浏览器中保存生产凭据。

## 本地运行

```bash
cd Frontend/OpenAgent.Chat
pnpm install
pnpm dev
```

在设置窗口填写 Router 地址，例如 `http://localhost:5001`，再填写租户并登录开发账号。

## 验证

```bash
dotnet build Backend/OpenAgent.sln -c Release --no-restore
dotnet test Backend/OpenAgent.sln -c Release --no-build

cd Frontend/OpenAgent.Chat
pnpm test
pnpm build
```

前端诊断页可检查 Gateway Live、Ready、Agent Catalog、Identity 和 Conversations。Vite 可能报告第三方 PURE 注释和大 Chunk 提示，目前不影响构建结果。
