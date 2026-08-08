# Chat 工作台验证报告

## 验证范围

本报告验证浏览器只连接 Router 时，健康检查、身份、Agent 目录、会话、管理配置和自动意图路由聊天均可到达模拟 Engine。验证日期为 2026-08-08。

## 自动化结果

在仓库根目录执行：

```bash
dotnet build Backend/OpenAgent.sln -c Release --no-restore
dotnet test Backend/OpenAgent.sln -c Release --no-build
cd Frontend/OpenAgent.Chat
pnpm install
pnpm test
pnpm build
```

结果：

- Backend Release 构建成功，0 警告、0 错误；
- Backend 138 个测试全部通过，其中 Router 29 个；
- Frontend API 3 个 Vitest 测试全部通过；
- Vue TypeScript 检查与 Vite 生产构建成功。

Router 新增测试覆盖：认证拦截、无 Engine 的 503、路径和查询串保持、可信身份覆盖，以及匿名鉴权请求清除伪造内部 Header。Frontend API 测试覆盖 Gateway 身份 Header、SSE 与路由 Header 解析、Problem Details 和 Trace ID 错误展示。

## 真实链路联调

联调拓扑：

```text
OpenAgent.Chat (127.0.0.1:5620)
  -> OpenAgent.Router (127.0.0.1:5631)
  -> Fake Engine (127.0.0.1:5632)
```

浏览器完成了以下场景：

| 场景 | 结果 |
|------|------|
| Gateway Basic 登录与租户上下文 | 用户 `reviewer`、租户 `tenant-smoke` 正确显示 |
| 工作台诊断 | Live、Ready、Agent Catalog、Identity、Conversations 全部通过 |
| Agent 目录 | 展示 3 个模拟 Agent |
| Auto 意图路由 | `intent-router` 从 Finance/Support 中选择 Support |
| SSE 聊天 | 页面实时显示 `Routed through support.`，状态更新为 `Completed` |
| 路由可解释性 | 右侧上下文显示“意图路由结果”和实际 Support Agent |
| 会话读取 | Router 透传列表与详情请求 |
| 管理读取 | LLM、Agent、MCP、Skill、RAG 标签页均通过 Router 加载 |

下游请求证据显示，聊天请求包含 Router 解析后的 `X-OpenAgent-Resolved-Agent-Id: support`；身份、会话和管理请求使用 Router 重建的 `X-User-Id: reviewer` 与 `X-Tenant-Id: tenant-smoke`。

## 已知构建提示

Vite 构建仍报告第三方依赖中的 PURE 注释位置提示，以及单个 JavaScript Chunk 超过 500 kB。它们不影响本次构建和运行结果，但后续可通过按设置模块动态加载 Element Plus 组件来缩小首屏包体。
