# OpenAgent.Chat

Vue 3 + TypeScript + Vite 单页工作台。浏览器只连接 OpenAgent Router（Gateway），由 Router 统一处理 Agent 目录、意图选路、Engine 服务发现、外部 Agent 转发、身份和管理 API 透传。

## 本地开发

```bash
pnpm install
pnpm dev
```

在设置窗口填写 Router 地址，例如 `http://localhost:5001`，再填写租户并使用开发环境 Basic 账号登录。当前 Basic 实现仅用于本地联调：它只解码凭据，不校验密码，不能作为生产认证方案。

## 验证

```bash
pnpm test
pnpm build
```

工作台内的“诊断”页会从浏览器验证 Gateway Live、Ready、Agent Catalog、Identity 和 Conversations。架构、接口和安全边界见 [`docs/modules/chat-workspace/`](../../docs/modules/chat-workspace/README.md)。

## 生产边界

生产环境应在 Gateway 接入企业身份提供方与统一权限策略。浏览器不应直接访问 Engine，也不应信任客户端提交的内部身份或已解析 Agent Header。
