# 2026-10-19 OpenCode ACP 运行时验证

范围：平台接入 [PR #234](https://github.com/philfanzhou/OpenAgent/pull/234)，依托 ACP SDK [PR #233](https://github.com/philfanzhou/OpenAgent/pull/233)；真实 OpenCode 固定 1.18.35，开发环境为 macOS arm64、.NET SDK 10.0.301。源码/目录/配置见 [运行时文档](../integrations/opencode-runtime.md)，当前状态见 [任务清单](../planning/TODO.md)。

## 已完成的定向验证

| 场景 | 结果与边界 |
|---|---|
| SDK 格式与中文注释 | PR #233 已推送；SDK 121 项自动化测试通过，包含真实控制台进程场景 |
| 生产默认平台执行入口 | AcpExecutionTests 5 项通过；不调用 MAF 推理，工具回调授权、历史续聊、保存先于终态、取消/失败部分结果均已覆盖 |
| 驱动与 MCP 桥 | Runtime.Tests 8 项通过，其中 5 项使用真实 OpenCode + 本地模型；覆盖中文历史、工具回喂、取消回收、远端错误脱敏、配置宏不展开、环境白名单、工具增量合并与 bearer 校验 |
| HTTP/SSE 完整链路 | OpenCodeSseTests 通过；真实 Kestrel → 默认 AgentExecutor → OpenCode → C# 用户工具 → SSE → 平台存储 → 第二轮历史；存储为内存替身，不是 PostgreSQL |
| OpenCode Linux 打包 | Engine Dockerfile 的 opencode 阶段在 Linux arm64 构建成功，容器内运行原生二进制得到 1.18.35 |

## 最终回归与镜像

- 完整解决方案构建通过：0 错误；保留现有 ImageSharp 3.1.12 的 NuGet 漏洞告警，未在本轮扩大依赖升级范围。
- 最终全量回归：**1059 通过 / 24 环境门控跳过 / 0 失败**，共 1083 项。设置 OPENAGENT_TEST_OPENCODE 开启真实 harness 场景，默认跳过的 Bubblewrap/Runner 用例未伪装成通过。
- Application.Tests：294 通过、2 跳过；包括默认 ACP 入口、Skill 资源参数绑定、初始化清理、取消前先回收驱动再释放会话锁。
- Runtime.Tests：8 通过，其中 5 项真实 OpenCode；Engine.Tests：219 通过，其中包含真实 HTTP/SSE 平台链路。
- SDK：121 通过；架构：14 通过。其余项目由上述全量回归覆盖。
- 完整 Engine 运行镜像在 Linux arm64 构建成功；最终镜像内 opencode --version 返回 **1.18.35**。
- Dockerfile 的 runtime-tests 阶段在最终源码上重跑：Runtime.Tests **8/8**、OpenCodeSseTests **1/1** 通过。未启动依赖真实数据库/身份提供方的生产宿主。
- 本轮修改的文档相对链接检查通过，无断链；源码差异无空白错误。

复现：先 dotnet build Backend/OpenAgent.sln，再设置 OPENAGENT_TEST_OPENCODE 运行 dotnet test Backend/OpenAgent.sln --no-build -m:1。Linux 执行 Dockerfile 的 runtime-tests 目标，详见 [运行时文档](../integrations/opencode-runtime.md)。

## 验收限制

没有使用外部模型凭据、调用付费模型或部署服务。Chat Completions 的模型传输通过本地确定性端点验证；Responses/Anthropic 尚无对应真实模型互操作证据。没有进行生产并发容量、操作系统隔离、数据库迁移/生产持久化、Worker、RunGrant、人工审批或跨进程 session/load 验收。

MAF 历史/Skill 兼容和独立摘要客户端仍在 Application。原 MAF 聊天循环仅保留为测试夹具，不能用旧回归测试代替新的 ACP 路径验证。计费 TokenUsage 保持 null，未把上下文水位当作账单数据。
