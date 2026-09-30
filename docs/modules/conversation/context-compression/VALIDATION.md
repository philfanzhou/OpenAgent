# 上下文压缩验收

验证日期：2026-09-30。基线 `origin/main`：`9ba17fbf`，实现分支：`codex/conversation-compaction`。

## 回归

| 检查 | 结果 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | 通过，0 warning / 0 error |
| `dotnet test Backend/OpenAgent.sln --no-build` | 923 通过，24 环境门控跳过，0 失败 |
| `pnpm test`，工作目录 `Frontend/OpenAgent.Chat` | 17 文件、153 测试通过 |
| `pnpm build`，同上 | 通过；Vite 提示已有大 bundle，可独立优化 |
| `git diff --check` | 通过 |

跳过项是 macOS 无法运行的真实 Bubblewrap/Runner 用例；不将跳过计为通过。CI 的 Linux Runner 检查另行执行。

新增和更新的关键测试：

- `AgentCompactionTests`：同步和流式实际 MAF/AgentExecutor 调用，自动压缩落库后由新 DI scope 继续下一轮；手动服务释放锁后继续；摘要失败恢复原历史。
- `TurnCompactionStrategyTests`：按用户轮次保留、超大近期轮次调整、单轮长工具循环、并行工具展开序号、超长中文 transcript 分段与总预算。
- `AuditedCompactionStrategyTests`：失败/取消恢复，审计落库失败时拒绝提交压缩上下文。
- `ConversationSessionStoreTests`：新投影重复用户文本不丢失，旧自动投影的未落库重叠兼容。
- 前端状态测试：重复点击、切换会话、POST 响应丢失后恢复审计、详情刷新失败保留成功结果、压缩中阻止发送并保留草稿、失败原因展示。

## 实际 HTTP、存储与浏览器

启动本分支 Engine.Host 与 Chat，使用隔离的 PostgreSQL 数据库 `openagent_compaction_ac28`、独立 Redis，启用 `ConversationCache:Enabled=true`。模型端使用可控的 OpenAI Chat Completions 模拟服务：拒绝无 user 消息的请求，检查小窗口摘要输入加输出额度，并支持同步与 SSE。此层验证真实平台、协议、存储和 UI，不验证商业模型的摘要质量。

实际 HTTP 会话 `compaction-http-v4`：先使用 16384 窗口完成四轮，执行手动压缩；随后调整模型窗口为 4096，发送较长的当前请求触发自动压缩。

| 审计 | 结果 | 原始边界 | 摘要覆盖范围 | 投影版本 | 原估算 → 压缩后估算 |
|---|---|---|---|---|---|
| Manual | Succeeded | 8 | 1–4 | 1 | 6668 → 3362 |
| Automatic | Succeeded | 9，含未提交的当前 user | 1–8 | 1 | 5399 → 2065 |

最终原始消息数 10。后续回复位于边界之后，加载模型历史时自动追加。请求捕获确认调用包含当前 user；自动摘要分两段，输入估算分别 3181、917，输出额度均 819，均在 4096 窗口内。摘要请求的 role 为 system + user，旧工具调用作为引用数据。

浏览器通过 Basic 开发登录访问本地 Engine，在默认窄视口中完成四轮对话：

1. 输入区「压缩上下文」可点击；手动卡片显示成功、摘要、原始范围和 1452 → 752 的 token 估算。
2. 调低模型窗口后继续发送；出现「自动 · 摘要」成功卡片，原请求正常续接并返回回复。
3. 操作结束后按钮恢复可用；时间线同时保留手动和自动审计。当前轮 usage 与压缩审计分别展示。

浏览器截图由本次验收保存并随交付展示，不作为模型语义正确性的证明。

## 边界

预算使用 MAF/UTF-8 估算，不是 provider tokenizer 的精确值。当前 user 请求、系统指令和最新完整工具组有保留底线；若这些内容本身超出模型窗口，摘要旧历史不能保证请求可接受。超大工具响应的检索/分页协议属于独立问题，见 [RESEARCH.md](RESEARCH.md)。

真实商业模型上的摘要准确性、reasoning 输出行为以及长时间生产负载仍需部署后验证；本次没有将模拟模型验收写成生产实测。
