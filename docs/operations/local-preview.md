# 本机开发预览（PostgreSQL 持久化，免 Docker）

本页描述 macOS/Linux 本机预览。需要 .NET 10 SDK、Node/pnpm、OpenCode（版本不固定）、Python 3 和原生 PostgreSQL。Redis、Docker、MinIO 不属于本机预览依赖。代码入口为 `scripts/local-preview.py`。

## 启动与停止

在仓库根目录执行：

```sh
python3 scripts/local-preview.py up --opencode /absolute/path/to/opencode
python3 scripts/local-preview.py status
python3 scripts/local-preview.py down
```

OpenCode 在 PATH 中时可省略 `--opencode`；脚本也会查找 `<data-root>/tools/node_modules/.bin/opencode`，该持久目录已安装 OpenCode。脚本编译后端、安装锁定的前端依赖，启动原生 PostgreSQL、Engine 与前端。PostgreSQL 工具从 PATH 或 macOS Homebrew 安装目录寻找，也可传 `--postgres-bin /path/to/bin`。已有 PostgreSQL 可通过 `--database 'Host=...;Database=...;Username=...;Password=...'` 或环境变量 `ConnectionStrings__OpenAgentDatabase` 连接。

默认页面为 `http://127.0.0.1:5184`，Engine 为 `http://127.0.0.1:5240`，本机 PostgreSQL 为 `127.0.0.1:55432`。可用 `--web-port`、`--engine-port`、`--postgres-port` 覆盖；端口占用时报错。登录使用开发账号 `admin / admin` 或 `test / test`。

脚本创建的 PostgreSQL 集群和对象文件默认位于 macOS 的 `~/Library/Application Support/OpenAgent/local/`，Linux 的 `~/.local/share/openagent/local/`，可通过 `--data-root` 覆盖。数据库初始密码随机生成并保存在该目录的 owner-only 文件中。模型密钥继续通过服务端 Data Protection 加密；保留服务账户的持久化密钥环才能解密既有记录。

`down` 停止脚本登记的预览进程，**不删除数据库或文件，也不停止独立的 PostgreSQL 进程**。需要手动停止脚本创建的数据库时，用 PostgreSQL 的 `pg_ctl -D <data-root>/postgres -m fast -w stop`；下次 `up` 会重新启动并使用已有数据。进程和日志记录在忽略提交的 `.preview/local/`。

开发宿主启动时按 `Database:ApplyMigrationsOnStartup` 应用 migration，迁移历史明确位于 `public.__EFMigrationsHistory`，不受 PostgreSQL 默认 `"$user", public` 搜索路径影响。生产迁移仍由部署流水线显式执行。

## Runtime、模型与资源

1. 在“Runtime 设置”新增并保存名称、ID、运行时类型和单轮时限。OpenCode 可选；Claude Code、Codex CLI、Gemini CLI 显示为禁用，后端也拒绝保存这些未启用类型。
2. 会话上方选择已保存的 Runtime。每次请求通过 `context.runtimeId` 选择当前租户的记录；实际单轮时限取该记录与宿主上限的较小值。旧 API 客户端未传 Runtime ID 时保留默认 OpenCode 行为；工作台发送要求选择已保存项。
3. 模型、Skill、MCP 的设置入口和能力绑定保持原位置。Agent 编辑器已移除旧 OpenCode 内嵌模型配置；聊天顶部独立选择已保存的 LLM 连接，未选择时阻止发送。
4. 回复下方的“实际运行时 · OpenCode 版本号”由后端在原生进程完成 ACP 握手后返回，同时显示 Runtime 名称；随会话持久化，刷新后仍可核对。旧回复没有握手记录时不补造标识。
4. Skill 上传、MCP 定义编辑、Agent 中的 Skill/MCP 绑定保持原位置，Runtime 记录不包含任何模型、Skill 或 MCP 绑定。

## 持久化与验收边界

Runtime、模型连接、Agent、Skill 元数据、MCP 定义、会话和消息写入 PostgreSQL；文件字节在本机受限目录持久化。Runtime 和 MCP 记录按 TenantId 隔离。旧 Redis/内联 MCP 配置仍可读取，新保存的定义优先使用 PostgreSQL。开发模式已移除进程内存储组合。

健康报告可能因未部署 Redis 显示 fallback/Degraded；独立的数据库健康项应为 Healthy。前端默认直连 Engine，已有连接偏好可在设置中调整。Router 意图路由不属于本机仅 Engine 的预览。

本机未验证 Linux Runner 沙箱。Skill 脚本和平台代码执行仍需单独配置 Runner；外部 MCP 鉴权头尚未提供配置入口。完整实测与重启验证随实现 PR #234，其他边界见 [运行时说明](../integrations/opencode-runtime.md)。
