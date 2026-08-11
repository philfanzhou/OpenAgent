# Chat 工作台验证报告

## 验证范围

本报告验证 PR 基于最新 Provider 路由架构重建后，后端 Gateway 接口、前端 API 契约和工作台页面可以独立构建运行。验证日期为 2026-08-11。

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
- Backend 153 个测试全部通过，其中 Router 36 个、Engine 52 个、Core 60 个、Hosting 5 个；
- Frontend API 3 个 Vitest 测试全部通过；
- Vue TypeScript 检查与 Vite 生产构建成功。

Router 新增测试覆盖：认证拦截、无 Engine 的 503、路径和查询串保持、可信身份覆盖，以及匿名鉴权请求清除伪造内部 Header。Frontend API 测试覆盖 Gateway 身份 Header、SSE 解析、Problem Details 和 Trace ID 错误展示。

## 浏览器视觉验证

本地启动：

```bash
cd Frontend/OpenAgent.Chat
pnpm dev --host 127.0.0.1 --port 5620
```

在 1440×900 桌面视口完成以下检查：

| 场景 | 结果 |
|------|------|
| 三栏工作台 | 会话侧栏、聊天区、路由/身份/会话上下文完整显示 |
| Agent 选择 | `Auto · 意图路由` 与手动 Agent 选择入口正常显示 |
| 对话输入 | 建议卡片、消息输入、附件入口和发送状态正常显示 |
| 设置窗口 | Gateway、诊断、LLM、Agent、MCP、Skill、RAG 标签完整显示 |
| 响应式布局 | 默认窄视口自动隐藏右侧上下文，桌面视口恢复三栏 |

本次视觉验证没有启动 Router/Engine，因此页面中的“连接失败”是预期状态；真实聊天链路由后端和前端 API 自动化测试覆盖其静态契约。

## 已知构建提示

Vite 构建仍报告第三方依赖中的 PURE 注释位置提示，以及单个 JavaScript Chunk 超过 500 kB。它们不影响本次构建和运行结果，但后续可通过按设置模块动态加载 Element Plus 组件来缩小首屏包体。
