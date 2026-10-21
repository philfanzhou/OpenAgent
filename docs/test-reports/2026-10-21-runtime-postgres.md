# 独立 Runtime 与本机 PostgreSQL 验收

日期：2026-10-21。基线：PR #234 的 `f7481ed1`。本记录覆盖用户澄清后的范围：模型连接、Skill、MCP 的设置和绑定保持原位置；仅新增独立 Runtime 管理与会话顶部选择，并把本机预览改为 PostgreSQL 持久化。历史内存预览事实见 [早期记录](2026-10-21-opencode-local.md)。

## 实现

- 新增租户级 `RuntimeProfile` 和独立 Runtime 设置页，保存 ID、名称、Provider、单轮时限；没有模型、Skill 或 MCP 绑定字段。聊天顶部列出已保存的 Runtime，选择通过 `context.runtimeId` 传入。刷新后保留所选项，删除当前项后重新选择，工作台未选 Runtime 时提示先保存并选择。
- Runtime 类型仅 OpenCode 可选；Claude Code、Codex CLI、Gemini CLI 在表单中禁用。伪造 PUT 请求选择这三种类型均返回 400，运行时驱动也有未启用 Provider 拒绝测试。
- 模型设置、独立 LLM 连接选择、Skill/MCP 设置页和 Agent 中的现有能力绑定保持原位置。已按最新要求移除 Agent 编辑器内嵌 OpenCode 模型配置，模型从独立连接选择。Runtime 选项不会重新绑定这些资源。
- 删除本地内存仓储组合及对应替身测试。本机所有元数据与会话使用既有 EF Core/PostgreSQL 实现，Skill/文件字节由开发环境磁盘对象存储持久化。
- 新增 `runtime_configurations`、`mcp_configurations`；补齐 `agent_configurations.OpenCodeJson` 的数据库映射和模型密钥加解密。新保存 MCP 优先从 PostgreSQL 读取，保留旧 Redis/内联读取路径。
- 迁移历史显式固定在 `public.__EFMigrationsHistory`。真实重启测试发现 PostgreSQL 默认 `"$user", public` 搜索路径在同名 schema 创建后会变化，未固定时会读取新空历史表并重复建表；已修复并新增无数据库依赖的配置回归。
- 操作方法、数据目录与停止方式见 [本机预览](../operations/local-preview.md)；表结构见 [配置存储](../database/tables/Configurations.md)。

## 本机持久化与真实调用

环境：macOS、.NET 10、OpenCode 1.18.35、原生 PostgreSQL 17.11，无 Docker。数据库监听 `127.0.0.1:55432`，数据库 `openagent_local`；磁盘对象与数据库集群位于 `~/Library/Application Support/OpenAgent/local/`。数据库健康项 Healthy；无 Redis 的 fallback/Degraded 仍保留。

1. 停止内存预览前导出当前模型、Agent、Skill、MCP 和会话，备份在忽略提交的 `.preview/local/pre-postgres-export.json`。迁入数据库后保留原有测试会话，包括用户之前输入的 `test` 消息。
2. 浏览器进入“Runtime 设置”，新增 `local-opencode / 本机 OpenCode`，实际展开类型列表确认 OpenCode 可选、其余三项 disabled，保存成功并出现在会话顶部。
3. 保存资源与所有会话详情的规范化 JSON 快照；停止 Engine/前端及原生 PostgreSQL 后重新启动。重启前后 Runtime、LLM、MCP、Skill、Agent 配置和会话内容逐项完全一致。Skill 字节继续从原目录读取，没有重新上传或重新注入连接。
4. SQL 只查询加密状态布尔值：独立 LLM 的 ApiKey 与 Agent OpenCodeJson 内的模型 ApiKey 均以 `v1.` 加密格式保存。密钥不进入源码、报告或截图。
5. 重启后浏览器选择持久化的“本机 OpenCode”与原 Agent，保持“使用 Agent 模型”，通过真实智谱 `glm-5.3-flash`（Anthropic Messages 兼容接口）重新加载 Skill 并实际调用 MCP。回复分别返回 `SKILL-LOCAL-38291` 与 `MCP-LOCAL-74621`；本轮显示两项工具操作，MCP 服务日志确认收到调用。

## 实际运行时身份与 Agent 编辑器复核

- Agent 编辑器实际打开核对：仅基础信息与能力绑定两节，旧 OpenCode 配置整节移除，MCP/Skill 原绑定保留。聊天顶部不再提供“使用 Agent 模型”，自动选择可用的已保存 LLM 连接；没有模型连接时阻止发送。旧服务端 API 回退仅为已有配置兼容，不作为新前端入口。
- 后端只在真实原生进程 ACP initialize 成功后返回运行时身份：Provider、Runtime ID/名称、对端 Agent 名称/版本；成功与失败路径均将已观察到的身份保存到消息元数据。未启动或未完成握手的调用不生成身份标识。
- 本次浏览器选 `local-opencode`、`local-review`、独立模型连接 `local-real`，真实调用智谱 glm-5.3-flash，重新加载 Skill 并调用 MCP；返回 `SKILL-LOCAL-38291` 与 `MCP-LOCAL-74621`。回复显示“实际运行时 · OpenCode 1.18.35 / 本机 OpenCode”。刷新后标识与内容保留；截图保存在忽略提交的 `.preview/local/runtime-identity.png`。
- 新增回归覆盖握手版本输出、SSE runtime 事件、执行成功/失败后的运行时元数据持久化、流式结束后历史重载保留身份、无独立模型时阻止发送。

## 自动化验证

- 后端完整构建通过，0 错误；既有 ImageSharp 漏洞告警未压制。
- 后端完整测试：**1035 通过、24 环境门控跳过、0 失败**；启用了真实 OpenCode 进程测试。跳过项仅 Linux Bubblewrap/真 Runner 条件不满足，不作为本机沙箱通过证据。
- 前端 `pnpm check`：**18 个文件、158 项测试通过**，类型检查与生产构建通过；保留 bundle 大小提示。
- 新增覆盖：Runtime/MCP 仓储跨实例读取与租户隔离、原有 Agent 模型和 Skill/MCP 绑定的数据库往返、迁移历史 schema、磁盘文件新实例读取及路径边界、未启用 Provider 拒绝、保存的 Runtime 时限覆盖宿主较长时限并释放工作区、请求 Runtime ID 映射与前端独立传递/未选择阻断。
- 迁移 `AddRuntimeProfilesAndDurableLocalConfiguration` 只新增表、列和默认值，不删除已有业务表；已在当前真实数据库应用，并完成重启与旧会话读取验证。

## 边界

Runtime 设置提供运行时选择和时限，不开放从浏览器提交可执行文件路径/任意命令。当前只有 OpenCode 驱动可用，置灰项不代表已实现对应集成。外部 MCP 鉴权头、其他服务商、Worker/RunGrant/审批、生产部署和计费用量边界沿用 [运行时说明](../integrations/opencode-runtime.md)。
