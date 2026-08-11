# OpenAgent.Chat

Vue 3 + TypeScript + Vite 单页工作台。支持连接 OpenAgent Router 或直连 Engine：Router 负责意图选路、Engine 服务发现和第三方 Agent 转发；直连模式用于单 Engine 开发联调。

## 本地开发

```bash
pnpm install
pnpm dev
```

在设置窗口分别保存 Router 地址（例如 `http://localhost:5001`）与 Engine 地址（例如 `http://localhost:5000`），选择本次连接模式，再填写租户并使用开发环境 Basic 账号登录。两套地址和当前模式保存在浏览器本地，切换模式不会覆盖另一套地址。当前 Basic 实现仅用于本地联调：它只解码凭据，不校验密码，不能作为生产认证方案。

## 验证

```bash
pnpm test
pnpm build
```

工作台内的“诊断”页会从浏览器验证当前 Router 或 Engine 的 Live、Ready、Agent Catalog、Identity 和 Conversations。架构、接口和安全边界见 [`docs/modules/chat-workspace/`](../../docs/modules/chat-workspace/README.md)。

## 生产边界

生产环境建议通过 Router 接入企业身份提供方与统一权限策略。直连 Engine 会绕过 Router 的意图识别、服务发现、外部 Provider 和 Router 权限边界，只适合受控网络或开发联调；服务端仍不得信任客户端提交的内部身份 Header。
