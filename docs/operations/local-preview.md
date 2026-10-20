# 本机开发预览（免 Docker）

本页描述 macOS/Linux 本机预览。需要当前分支要求的 .NET 10 SDK、Node/pnpm、OpenCode **1.18.35** 和 Python 3；不需要 Docker、PostgreSQL、Redis 或对象存储服务。代码入口为 `scripts/local-preview.py`，实测见 [本机验收记录](../test-reports/2026-10-21-opencode-local.md)。

## 启动与停止

在仓库根目录执行：

```sh
python3 scripts/local-preview.py up --opencode /absolute/path/to/opencode
python3 scripts/local-preview.py status
python3 scripts/local-preview.py down
```

OpenCode 在 PATH 中时可省略 `--opencode`。启动脚本编译后端、安装锁定的前端依赖，并启动 Engine 与前端；端口占用时直接报错。默认页面为 `http://127.0.0.1:5184`，Engine 为 `http://127.0.0.1:5240`，可用 `--web-port`、`--engine-port` 覆盖。进程和日志记录在忽略提交的 `.preview/local/`。

脚本只监听 loopback。登录使用开发账号 `admin / admin` 或 `test / test`。前端默认直连 Engine；已有浏览器连接偏好会保留，可在连接设置切换为“直连 Engine”。Router 意图路由不属于这个仅 Engine 的本机预览。

## 模型与资源

1. 在“LLM 配置”保存真实 Endpoint、接口格式、模型 ID、上下文窗口和 API Key；聊天头部选择这个已保存的连接。API Key 在查询中不回显，编辑留空会保留已保存值。
2. 也可在“Agent 配置 → OpenCode 运行时”开启内嵌模型并填写连接；头部选择“使用 Agent 模型”时不传独立模型 ID，由服务端使用该 Agent 的连接。显式选择独立 LLM 配置会覆盖 Agent 内嵌连接。
3. 在“Skill 配置”上传包，在“MCP 配置”保存远程 HTTP/SSE 服务，然后在 Agent 编辑器选择要绑定的资源并保存。OpenCode 在本轮工作区发现 Skill，外部 MCP 由 OpenCode 原生连接。
4. 发送消息后可查看工具调用与结果；完成的会话保存在当前预览进程中。

## 数据与验证边界

`LocalDevelopment:UseInMemoryStores=true` 只在 Development 环境生效，替换数据库和对象存储，使用同一份 Skill 目录供上传、绑定与执行读取。**本机预览的数据只在当前进程内保存，重启会丢失模型连接、Agent、Skill 文件和会话**；它不自动读取其他部署实例的数据。常规持久化部署继续使用数据库和对象存储，配置见 [部署指南](deployment.md)。

健康报告仍展示 Redis 缺失的 fallback/Degraded 状态；这不是本机预览启动失败。数据库检查在内存模式不注册。脚本用健康报告 HTTP 响应及前端响应确认启动，实际模型与工具调用需按上面步骤验证。

默认拒绝 OpenCode 原生终端与普通文件工具；Skill 脚本和平台代码执行仍需单独配置 Runner。本机预览未验证 Linux 沙箱，不把 macOS 跳过项当成沙箱通过。外部 MCP 鉴权头尚未提供配置入口，当前支持已保存的 URL/类型；含鉴权和其他生产能力的边界见 [运行时说明](../integrations/opencode-runtime.md)。
