# ACP 单包 SDK 完整实现实测记录

验证日期：2026-10-16。基于 `origin/dev` 的 `30fde6d5010fc2e57208a95912d7fa7600eaa3f9`，实现分支 `codex/acp-sdk-complete`，最终代码提交 `bcf6a2806efdbf16a78128278b5ecd650aeb5e36`。环境：macOS arm64、.NET SDK 10.0.301；当前 dev 已使用 net10.0。本记录只描述本次实测，任务状态见 [任务清单](../planning/TODO.md)。

## 结果

| 检查 | 实际结果 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | 0 错误；70 条既有 NuGet 依赖审计警告，集中于平台 ImageSharp 3.1.12，未改动该依赖 |
| `dotnet test Backend/OpenAgent.sln --no-build -m:1` | 1040 通过、24 跳过、0 失败；覆盖 9 个测试工程 |
| ACP SDK 测试 | 121 通过、0 跳过、0 失败；含实际控制台进程的 16 个离线场景 |
| 官方 SDK 双向互通 | TS / Python / Rust 三组双向，共 6 项 PASS，见 [原始结果](acp-sdk-complete/official-sdk-interop.json) |
| NativeAOT publish/run | osx-arm64 实际编译，无 AOT 警告；原生程序运行 16 个离线场景全部 PASS，见 [原始结果](acp-sdk-complete/aot-macos-arm64.json) |
| 压力场景 | 10 个 session 各 10,000 次更新，100,000 次全部收齐；原生运行耗时见 JSON 中 load 项，不能当作生产容量基准 |
| 单包检查 | 仅 `lib/net10.0/OpenAgent.Runtime.Acp.dll`；无第三方 runtime dependency、测试/控制台/schema 载荷；含 README、MIT、仓库元数据 |
| 符号与 SourceLink | 本地生成 `0.1.0-preview.nupkg` / `.snupkg`；SourceLink 映射到上述代码提交，未上传 NuGet |
| 固定协议来源 | 实际下载固定 commit 的官方 schema/meta，SHA256 与方法/14 更新/五类内容摘要匹配 |

完整构建的依赖审计告警属于现有平台依赖，不等于本 SDK 的编译告警；SDK、控制台和 AOT 构建无警告。24 项跳过为已有真实 Runner / Bubblewrap / MAF 集成环境门控，本地未启用；不能解释为对应生产能力通过。

## 实际运行与边界

SDK 测试覆盖：分片与原子写入、所有权、双向同号重入、100 并发响应关联、一般取消、batch 后紧随取消、无响应通知、非法 wire、EOF、阻塞写期限、会话创建配额、早到通知、关闭后队列尾部完整性、慢消费故障、权限默认拒绝/非法选项/待审批取消、未确认 prompt 取消、form 校验、URL 完成关联、terminal-auth 类型及退出码、真实路径/符号链接、终端 UTF-8/跨 session/配额/kill/release、大量 stderr 和自有进程强制收敛。协议测试还验证普通 getter 无重复分配、`rawInput:null` 与省略的区分。

官方 Client → C# Agent：初始化、new、中文/表情流式、反向 permission、session/cancel 与原 prompt 的 cancelled 响应。C# → 官方 Agent：初始化、new、流式 hello，以及收到 started 后取消并验证最终 cancelled。TS 使用 1.8.0；Python 使用固定官方 commit 的 0.12.1 源码，测试时以 PYTHONPATH 指向检出的原始包，未使用自写替代 SDK；Rust 使用固定官方 git rev 的 3.3.0 与 schema 1.11.0。夹具、依赖锁定及可复现步骤见 [验证文档](../integrations/acp-dotnet-sdk/VALIDATION.md)。

首次并发回归曾出现控制台 load 丢计数，定位到释放者与消费者竞争读取尾部队列，已修复并增加确定性回归。另有一次既有 Router readiness 用例在并行负载下超时；最终完整测试采用单项目调度通过，未修改 Router 或放宽其断言。一次测试成功不代表压力环境不会再次暴露宿主调度问题。

## 未验证范围

- 新增三 OS CI 模板尚未启用：GitHub 拒绝含工作流的首次推送，原因是当前 OAuth 缺少 workflow scope；配置已改为文档中的模板，需具备权限的维护者启用。现有后端 CI 的远端结果创建 PR 后单独查看；本机不能证明 Linux/Windows 的 AOT 运行已通过。Windows 的符号链接反例在现有文件测试中按平台条件跳过该分支，常规路径边界仍执行。
- 真实模型推理、登录/付费账户、真实 Agent 生产配置、官方 TCK、HTTP/v2/unstable 协议。
- 平台 Worker / MAF 替换、跨租户授权、持久化恢复和部署；本次没有改变现有平台执行路径。
- 完整 JSON Schema 格式校验与逐成员 XML 参考手册；表单只校验文档列明的基础子集，额外格式由宿主负责。

八阶段实现范围见 [阶段任务](../integrations/acp-dotnet-sdk/IMPLEMENTATION.md)。本地离线验收不替代稳定版发布评审或生产 E2E。
