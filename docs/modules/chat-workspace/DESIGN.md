# Chat 工作台设计

## 定位

`Frontend/OpenAgent.Chat` 是面向开发、联调和平台管理的单页工作台。它只配置一个 Gateway 地址，不直接感知 Engine 实例地址。

请求路径如下：

```text
Browser -> OpenAgent.Router -> Engine or External Agent
```

Router 是浏览器流量的信任边界，负责：

- 对未显式指定 Agent 的聊天请求调用意图识别 Agent；
- 聚合各 Provider 的可见 Agent 目录，并转发聊天和附件流；
- 根据会话与租户选择 Engine；
- 将第三方 Agent 请求交给所属的 Agent Provider；
- 为 Engine 重建可信用户、租户、会话和 Trace Header；
- 透传身份、会话和管理 API，使工作台不需要绕过 Gateway。

## 工作台能力

主界面采用三栏工作区：左侧管理会话，中间进行 SSE 聊天，右侧显示路由、身份和会话上下文。顶部 Agent 选择器支持两种模式：

- `Auto`：不提交显式 Agent，由 Router 的意图识别 Agent 选择目标；
- 显式 Agent：用户直接选择 Engine Agent 或外部 Agent。

设置窗口集中管理 Gateway、诊断、LLM、Agent、MCP、Skill 和 RAG。对应调用均使用 `Frontend/OpenAgent.Chat/src/api.ts`，目标是 Router 的 `/api/v1/*` 路径。

## Router 透传边界

`Backend/src/OpenAgent.Router/Endpoints/GatewayProxyHandler.cs` 处理以下 Gateway 路径：

- `GET|DELETE /api/v1/agent/conversations/{conversationId}`；
- `GET /api/v1/agent/me`；
- `GET|POST|PUT|DELETE|PATCH /api/v1/admin/{**path}`；
- `GET|POST /api/v1/auth/{**path}`。

需要身份的请求必须先通过 Router 认证。转发前移除客户端伪造的内部 Header，并依据 Router 用户上下文重新写入可信值。匿名鉴权请求同样清除 Agent、用户、租户和会话 Header，只保留新的 Trace ID。

## 安全约束

当前 Basic 认证仅用于开发联调：实现只解码用户名和租户，不校验密码。因此 Router 仅在 `Development` 环境映射 `/api/v1/auth/**` 与 `/api/v1/admin/**`；生产环境必须在 Gateway 接入真实身份提供方和统一权限策略后再开放管理面。

工作台把开发凭据保存在 `sessionStorage`，Gateway 和租户配置保存在 `localStorage`。不要在公共终端或共享浏览器中保存生产凭据。
