# 2026-10-21 OpenCode 进程保活复用与准备链路优化验证

范围：OpenCode 运行时延迟优化的第一批落地——空闲进程保活复用（`OpenCodeProcessKeeper`）与准备链路提速（并行化、Skill 内容缓存、配置读缓存、版本不固定）。实现要点见 [运行时文档](../integrations/opencode-runtime.md)，任务状态见 [任务清单](../planning/TODO.md)。

## 实现：进程保活复用

- 复用键 = 会话归属（租户/用户/会话哈希）+ 启动身份（实际可执行文件、参数与受控环境哈希；覆盖模型端点/密钥、Agent 指令与 MCP/Skill 放行策略），不跨会话归属复用进程。
- 进程主目录（进程工作目录 + HOME/USERPROFILE/XDG/OPENCODE_CONFIG_DIR）随进程生命周期，不再放在轮次工作区内；每轮随机工作区（`t-*`）仅作为 ACP 会话 cwd，轮次结束即删除（失败时后台延迟重试，应对保活进程短暂保留句柄）。保活进程主目录为 `p-*`，随进程销毁回收。
- 完整成功（`end_turn`）的轮次归还保活池；失败、取消、进程死亡、空闲超过 `KeepAliveSeconds`（默认 300，0 关闭）、累计会话超过 `MaxSessionsPerProcess`（默认 64）或保活总数超过 `MaxKeptProcesses`（默认 8）即销毁并回收主目录；后台清扫周期不超过 5 秒。
- 复用轮次跳过进程启动与 ACP initialize，`RuntimeStarted` 身份事件由缓存的对端身份发出；每轮仍新建 ACP session、loopback MCP 桥（随机 bearer）与随机工作区；冷路径上桥启动与进程获取/启动并行执行。

## 实现：准备链路优化

- `RuntimePreparation` 并行化：历史加载、平台工具发现与各 feature 工厂（MCP 定义解析、Skill 物化）并行执行后按注册顺序合并；EF 仓储均按调用创建独立 DbContext，天然并发安全；工厂同步/异步失败时已启动的任务进入统一释放路径，不泄漏已创建的 feature。
- `SkillPackageCache`：Skill 包按（租户、对象键、包 SHA-256）内容寻址缓存到进程级目录，首次逐文件下载+完整性校验后由 staging 原子提交；追问轮次零下载，发布新包自然失效。未登记哈希的记录退回一次性目录随轮释放。缓存条目上限 256（按最近使用淘汰），中断残留的 staging 目录按小时级宽限回收。
- `McpConfigRepository` / `RuntimeProfileRepository`（均单例）内置 30 秒 TTL 读缓存（含负结果），`SaveAsync`/`DeleteAsync` 即时失效；跨进程/跨实例最多 30 秒陈旧窗口。
- 版本不固定：仓库不再固定 OpenCode 互操作版本。Dockerfile 默认安装最新发布（`--build-arg OPENCODE_VERSION=<version>` 可锁定复现），文档与测试提示同步；与版本相关的测试断言改为语义断言（usage 只允许上下文占用口径，见下）。

## 验证

- 全解决方案回归（真实 OpenCode 开启）：**0 失败**。其中 Runtime.Tests 45/45（含 7 项新增保活测试：身份哈希稳定性、追问复用、跨会话隔离、失败不归还、空闲清扫、禁用回退、会话上限退役）、Engine.Tests 215/215（含真实 HTTP/SSE → OpenCode → 平台工具 → 持久化 → 续聊）、Application.Tests 264/266（2 项既有环境门控跳过，含 5 项 Skill 包缓存新测试）、Framework.Tests 36/36（含仓储 TTL 缓存/写失效测试）、Architecture.Tests 14/14。
- 计时探针（本地确定性模型，Windows x64，真实 harness）：首轮（进程冷启动 + JIT）约 3.16 s，复用轮约 0.96 s（约 -70%）；稳态新进程轮约 0.90–0.99 s，复用轮约 0.83–0.93 s。准备链路并行化与缓存未单独计时（收益取决于历史长度、启用 Skill/MCP 数量，代码路径由上述回归覆盖）。探针为一次性脚本，未保留在仓库。

## 环境偏差与限制

- 本机验证使用 npm 包内原生二进制 `node_modules/opencode-ai/bin/opencode.exe`，版本 **1.19.3**。仓库已不固定版本，1.19.3 与固定版本时期的两处行为差异已按语义收敛：harness 上报 `usage_update` 时终态/持久化携带上下文占用（prompt=total、completion=0，不伪造计费拆分），测试改为版本无关断言。
- 保活复用放宽了"每轮全新进程"的进程级隔离：同一会话在窗口内共享 harness 进程及其状态目录缓存。会话、工作区、MCP 桥与 bearer 仍每轮独立；跨会话、跨租户不共享进程。这不是操作系统沙箱，与既有边界声明一致。
- 未验收：生产并发容量下的内存驻留画像（保活进程上限为 `MaxKeptProcesses`）、多实例部署下配置缓存 30 秒陈旧窗口的产品影响、Skill 缓存在共享临时目录多 Host 同机部署下的并发行为（staging 原子提交已防交错，未做跨进程压测）。
