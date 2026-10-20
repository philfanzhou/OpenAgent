# OpenCode 本机运行与真实模型验收

日期：2026-10-20 至 2026-10-21。基线：PR #234 的 `6f3c8ef5`；本轮修复随该 PR 推送。环境为 macOS、本机 .NET 10、Node/pnpm、OpenCode 1.18.35。全程不使用 Docker，也不依赖 PostgreSQL/Redis/对象存储服务。任务状态见 [任务清单](../planning/TODO.md)。

## 审查发现及修复

| 问题 | 修复与验证 |
|---|---|
| 开发配置固定 `cmd.exe` | 使用跨平台默认 `opencode acp`；支持宿主配置绝对路径 |
| Options 的默认数组被绑定追加，启动成 `opencode acp acp` | 参数属性从空数组绑定，再为未配置场景补默认值；默认/显式配置回归覆盖 |
| 内存模式仍注册强制数据库健康检查 | 只在持久化模式注册数据库检查；实际本机宿主启动成功 |
| 内存对象键缺少租户分区，Skill 上传后加载失败 | 对齐租户/用户分区，避免跨租户同 ID 覆盖；执行有界读取；真实上传、绑定、加载通过 |
| 上传与执行使用两个不同 Skill 目录 | 移除内存目录覆盖，复用已有支持无 Redis 的仓储目录 |
| 远程 MCP 的 ACP 条目缺少必填 `headers` 数组 | 无配置头时显式发送空数组；真实 OpenCode + HTTP MCP 调用通过 |
| Skill 准备失败时工作区未进入清理范围；名称允许 `.` / `..` | 将物化放入清理范围并约束名称；路径拒绝回归与完整轮次清理通过 |
| 内存会话固定匿名用户，登录后列表身份不符 | 通过请求 accessor 逐请求读取身份；admin/test 身份切换回归覆盖 |
| 模型列表响应与保存数据共享对象 | 读写深拷贝；脱敏列表后保留已保存密钥的回归通过；移除未使用的 LLM 版本记录 |
| Agent 内嵌模型可保存，但前端禁止不选独立模型发送 | 增加“使用 Agent 模型”，去掉发送端对独立模型 ID 的强制要求，保留显式连接覆盖；真实页面两种选择均验证 |
| 本机预览默认走 Router，而仅启动 Engine | 启动脚本指定 Engine 模式，支持环境默认及用户显式偏好；前端回归覆盖 |
| 开发登录提示与内置账号规则不一致 | 提示 `admin/admin`、`test/test`，页面实际登录通过 |
| 旧目录覆盖移除后 Host 不再直接使用 Capabilities | 删除多余 ProjectReference，收紧架构依赖断言，并同步开发文档 |

## 自动化结果

- 后端完整构建：0 错误。保留已有 ImageSharp 3.1.12 的 NuGet 漏洞告警，没有压制。
- `OPENAGENT_TEST_OPENCODE=/absolute/path/to/opencode dotnet test Backend/OpenAgent.sln --no-build`：**1033 通过、24 跳过、0 失败**。
- 通过数：Contracts 58、Architecture 14、Framework 32、Runner 36、Runtime.Acp 114、Application 262、Router 124、Capabilities 153、Runtime 20、Engine 220。
- 24 项跳过均为 Linux Bubblewrap / 真 Runner 环境门控，不计为沙箱已验证。
- 前端 `pnpm check`：**18 个测试文件、155 项测试通过**，类型检查与生产构建通过。保留既有 bundle 大小提示。
- 新增真实 harness 回归 `SkillsAndExternalMcp_AreLoadedAndCalledByRealHarness`：确定性本地模型要求 OpenCode 调用原生 `skill`，确认正文中的测试标记，再要求调用远程 MCP 并确认结果回到模型。验证完成后的工作区清理及原始 Skill 包保留。

## 真实模型与页面验收

实际服务商为智谱 Anthropic Messages 兼容接口，模型 `glm-5.3-flash`。使用本机已有连接建立当前测试平台的模型配置，再经平台保存、加密、读取与授权交给 OpenCode；未把密钥写入源码或本报告。该结果不代表官方 Anthropic Claude 模型验收。

1. 通过当前平台管理 API 保存独立模型连接，执行聊天流，得到真实中文回复。
2. 上传 `local-verification` Skill 包，保存本机 HTTP MCP 定义，在 Agent 中绑定这两个资源。Skill 验证值 `SKILL-LOCAL-38291` 只存在于 Skill 正文；MCP 验证值 `MCP-LOCAL-74621` 由工具返回。
3. HTTP/SSE 记录同时包含原生 `skill` 调用/结果和 `local-verification_verification_code` 调用/结果，终态 `done` 返回模型 ID。
4. 浏览器使用 `admin/admin` 登录，在 LLM 编辑器留空密钥保存成功；Agent 编辑器能够显示并保存 Skill/MCP 绑定。
5. 在 Agent 中保存内嵌连接，聊天头部选择“使用 Agent 模型”，从页面发送请求，得到两个正确验证值及两个工具活动；该请求不传独立 LLM profile ID。
6. 切换到头部的独立模型连接后继续同一会话，验证读取平台历史并复述前轮结果。页面保持可供人工继续测试。

站点启动/停止已通过 `scripts/local-preview.py` 实测；操作和数据生命周期见 [本机预览](../operations/local-preview.md)。本机测试配置与会话保存在当前进程内，重启即失；本轮没有连接其他平台实例的历史数据库。

## 验收边界

- Chat Completions 用真实 OpenCode + 确定性模型验证；真实服务商验证覆盖智谱 Anthropic 兼容接口。OpenAI Responses、官方 Claude 服务、其他远程 MCP/SSE 服务商仍未逐一验收。
- 外部 MCP 当前仅支持平台已保存的 URL/类型，尚无鉴权头配置入口；外部调用由 OpenCode 原生执行，不经过平台工具预算/逐调用授权。
- 本机预览继续展示 Redis fallback/Degraded 健康项；数据库检查不注册。缺失 Redis 不阻断聊天与本机资源配置。
- Runner 脚本、操作系统沙箱、跨进程 session/load、Worker/RunGrant/审批、生产部署与计费 token 仍是独立范围；没有可靠用量时前端显示不可用。
