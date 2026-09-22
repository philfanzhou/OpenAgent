# 主流 Agent 工具设计调研与 OpenAgent 工具层改造方案

> 状态：提案（Proposal，阶段 0 已实施）｜日期：2026-09-22｜范围：`OpenAgent.Core/Capabilities`、执行引擎工具编排、Runner 协同

> **实施记录（2026-09-22，阶段 0 契约硬化已落地）**：
> - `OpenAgent.Contracts/Capabilities/ToolResult.cs`（结构化契约 + 错误信封工厂）；
> - `CapabilityDefinition.Invoke` 改为 `Task<ToolResult>`，四个内置能力源全部迁移（错误信封英文统一）；
> - `IsolatedToolFunction` 成为模型侧唯一出口：分级字符预算（读 80k/执行 40k/MCP 48k/默认 60k，`AgentExecution:*ToolResultCharBudget`）+ 头尾保留截断 + 收窄提示；未捕获异常脱敏（errorId 关联日志，`exception.Message` 不再透传模型）；
> - Schema 硬化：`additionalProperties:false`/参数 type/required 全量补齐；非法 schema 生产降级+Error 日志、Development 抛错；`BuiltInToolSchemaTests` CI 兜底；
> - 内置工具描述按"新员工手册"标准重写（write_file 不可修改已有文件等行为契约显式化）；
> - 漂移修复：`MaxTurns` fallback 5→`AgentConfig.DefaultMaxTurns`(50)；tool-calling/errors 文档更新。
> 验证：`dotnet build` 0 警告 0 错误；全解决方案 780 测试通过（新增 `ToolResultBudgetTests`、`BuiltInToolSchemaTests`）。
> **实施记录（2026-09-22，阶段 1 执行编排已落地）**：
> - 并行工具调用：`AllowConcurrentInvocation=true`；`CapabilityDefinition` 新增 `Concurrency`（ReadOnly/Exclusive，默认 Exclusive），ReadOnly 白名单（read_file/list_files/search_knowledge_base/get_current_user_profile/update_plan）真正并行，其余经每轮共享独占信号量串行（排队计入单次调用超时，错误文案可区分）；前置完成了 scoped DI 线程安全审查（仓储全走 IDbContextFactory，能力服务无状态）。
> - `update_plan` 计划工具：全量重发步骤（≤1 in_progress 校验），结果快照随工具结果落会话时间线；SSE 新增 `plan_updated` 事件（Host 帧名 plan_updated）。
> - MCP 连接池化：`McpClientPool` 按（租户,服务器）缓存客户端跨轮复用，每轮 ListTools 兼作健康探测，坏连接淘汰后重连一次，空闲惰性淘汰（`Mcp:ClientIdleTimeoutSeconds` 默认 600s）；作用域释放不再断开连接。
> 验证：`dotnet build` 0 错误；全解决方案 797 测试通过（新增 `ToolConcurrencyTests`、`PlanCapabilitySourceTests`、`McpClientPoolTests`；两处真 bug 被新测试拦截修复：非数组 plan 输入的未捕获异常、排队超时异常逸出隔离层）。
> **实施记录（2026-09-22，阶段 2 工作区与编辑范式已落地）**：
> - 会话沙箱 /work 从 tmpfs 改为宿主目录 bind-mount（`session-<key>/work`）：工作区状态跨沙箱重启存活，空闲回收仍由 WorkspaceReaper 扫描；SandboxReset 语义同步收紧（工作区仍在即不再误报 reset）。
> - Runner 新增 `/api/v1/workspace/{sessionKey}/list|read|write|edit|bytes/read|bytes/upload` 六端点（`WorkspaceStore`）：路径三层校验（字符规则 + 全路径包含 + 逐级符号链接拒绝），编辑语义为精确字符串替换（0 次未找到 / >1 次未允许全量替换均返回可行动错误），读取 cat -n 行号 + offset/limit 分页；同会话信号量串行。
> - Engine 新增 `WorkspaceCapabilitySource` 五工具（与代码执行同 gating、发现+调用双重 ACL）；`download_file` 可选 `workspacePath` 直落工作区；`export_workspace_file` 打通工作区→file asset→publish_files 交付闭环；workspace 读取类进 ReadOnly 白名单与读预算分类。
> - 选型维持 Claude 式 str_replace（跨 provider），未引入 patch 文法。
> 验证：clean 构建 0 警告 0 错误；全解决方案 824 测试通过（新增 `WorkspaceStoreTests` 16 项、`WorkspaceCapabilitySourceTests` 6 项、会话参数 bind 断言与 schema lint 纳入）。bwrap 真机路径（挂载/uid 映射/重置）依赖 Linux，由环境门控测试与部署验证覆盖。
> **实施记录（2026-09-22，P3 提前项：工具 token 成本治理，基于 #131）**：
> - 量化：16 个内置工具定义 ≈ 11.3k 字符（~2.8k tokens/请求，不含 provider 序列化开销）；AgentFactory 每轮 Info 日志记录实际水位。
> - MCP 延迟加载（原 P3 计划提前）：可见 MCP 工具 > `Mcp:DeferredToolThreshold`（默认 20）时改为单个 `search_tools` 检索入口，命中即激活并经 DeferredToolInjector（FICC 内层）逐轮注入（对标 Codex tool_search/defer_loading）。
> - ~~`AgentConfig.Tools.Disabled` 每代理工具裁剪~~：曾实现（含 EF 持久化与端到端验证，实测 1624→55 tokens/-97%），**后按维护者决定移除**——不在数据库存储工具选择、不做按代理禁用，留待以后需要时恢复（实现记录在 git 历史与本 PR）。
> 验证：841 测试通过；延迟加载已对真实 mock MCP server（25 工具）端到端验证（检索→激活→调用→执行）。

## 0. 结论先行

对标 Claude Code 与 OpenAI Codex CLI 后，OpenAgent 工具层的差距**不在工具数量，而在三层结构性缺失**：

1. **契约层缺失**（最严重）：工具结果是裸 `string`，无结构、无错误语义、无大小预算与截断。一次接近 1 MiB 的 `read_file` 或一个冗长的 MCP 结果可以直接吃穿上下文，而自动压缩默认关闭，对话没有恢复路径。
2. **编排层缺失**：`AllowConcurrentInvocation = false` 串行执行；无计划工具、无子代理；MCP 客户端每轮连接/销毁（每服务器每轮最多 30s init）。"工具配合差"的直接体感来源就是这一层。
3. **工具面缺失**：没有编辑范式（`write_file` 只能整文件新建、不能改已有文件）、没有检索工具、没有 web 工具、没有上下文管理工具。Agent 式（多轮读-改-写-验证）工作流在当前工具面上无法表达。

改造方案分四个阶段（详见 §4）：**契约硬化（P0，纯内核、收益立竿见影）→ 执行编排（并行+计划+MCP 池化）→ 工作区与编辑范式（最大架构缺口）→ 高层工具与治理（审批环、上下文管理、评测）**。

---

## 1. 调研：主流 Agent 工具是如何设计的

### 1.1 Claude Code（Anthropic）

工具集按抽象层级分三层（MinusX 的分析）：

| 层级 | 工具 | 设计意图 |
|---|---|---|
| 低层原语 | Bash、Read、Write、LS | 通用、兜底一切 |
| 中层组合 | Edit、MultiEdit、Grep、Glob | 高频操作专设工具，省 token、可加行为规则 |
| 高层确定性 | Task（子代理）、WebFetch、TodoWrite、exit_plan_mode | 一句话完成一长串低层操作 |

关键设计手法（引自提取的系统提示与工具描述，wong2 gist）：

- **描述即行为契约**。每个工具描述里写满了使用规则，例如 Read 要求"不要重复读已读文件"；Bash 里写"MUST avoid find/grep，用 Grep/Glob 工具；grep 必要时 ALWAYS USE ripgrep (rg)"；Edit 要求"必须先 Read、保留精确缩进、old_string 不唯一即失败"。Anthropic 官方说法是"像给新入职员工写文档一样写描述"。
- **频率 × 准确率决定工具拆分**：用得极频繁的操作（grep/glob/edit）值得单独做工具以换取准确率与 token 效率；低频操作留在 Bash 兜底。
- **并行调用 + 批处理引导**：系统提示明确"可以在一个响应里调用多个工具"，引导把 git status/diff/log 并行发、投机性 Read/Glob 批量发。
- **结果塑形**：Bash 输出超 30000 字符截断；Read 默认 2000 行、超 2000 字符的行截断、`cat -n` 行号格式（供 Edit 精确定位）；Anthropic 官方文档确认 Claude Code 工具响应默认上限 25,000 tokens，并配合"引导做更精准检索"的提示。
- **编辑范式**：精确字符串替换（`old_string`/`new_string`/`replace_all`），先读后写、唯一性校验、失败即报——对 JSON schema 友好，跨模型供应商可复制。
- **计划工具**：TodoWrite 维护任务清单（3 步以上任务强制使用、恰好一个 in_progress），是对抗 context rot 使用率最高的工具族之一。
- **子代理单层不嵌套**：Task 工具 spawn 只读探索型/通用型子代理，结果作为工具响应折叠回主历史，子代理不能再 spawn。
- **权限系统**：per-tool allow/ask/deny + hooks 拦截 PreToolUse。
- **小模型分工**：50% 以上的 LLM 调用走 Haiku（文件读取摘要、WebFetch 抽取、会话压缩）。

### 1.2 OpenAI Codex CLI（codex-rs，2026 当前主干）

从源码 `codex-rs/core/src/tools/handlers/` 一手取证，当前工具面远超早期"shell + apply_patch"：

- **`exec_command`（unified exec）**：参数含 `cmd`、`workdir`、`tty`、`yield_time_ms`（默认 10s，超时未结束则**返回 session_id**）、`max_output_tokens`（输出 token 预算，默认 10000，可被策略封顶）、`shell`、`login`、审批参数；配套 `write_stdin` 工具向仍在运行的会话写入 stdin。即**持久 PTY 会话 + 输出预算**是一等公民。
- **`apply_patch`**：以 **freeform 自定义工具 + Lark 文法（grammar-constrained decoding）** 实现——补丁格式由服务端文法约束保证合法，模型不包 JSON。注意：这依赖 OpenAI 自家 Responses API 的 custom-tools 能力，**跨供应商不可直接复制**。
- **`update_plan`**：`[{step, status: pending|in_progress|completed}]`，规则"同时最多一个 in_progress"。
- **`multi_agents` / `multi_agents_v2`**：`spawn`、`send_message`、`wait`、`interrupt`、`resume_agent`、`list_agents`——子代理编排工具化。
- **上下文管理本身也是工具**：`get_context_remaining`（返回剩余 token 数，带 output_schema）、`new_context`（开新上下文窗口、不影响环境状态）。
- **`tool_search`**：工具目录过大时按 query 检索**延迟加载（defer_loading）**的工具，而不是把全部定义塞进上下文。
- **`request_user_input`（含 async 版）**：向用户提问的正式通道。
- **shell 安全分析**：`codex-rs/shell-command/src/command_safety/` 用 tree-sitter 解析 bash/PowerShell 判定危险命令（含 Windows 危险命令表），配合 shell 快照与 `shell-escalation` 升级协议。
- **沙箱与审批是矩阵关系**：沙箱模式（读-only / 工作区可写 / 全访问，macOS Seatbelt、Linux Landlock/seccomp）× 审批策略（untrusted / on-failure / on-request / never），被沙箱拦截的操作走升级（escalation）请求。Codex 仓库同样 vendor 了 bubblewrap——与本项目 Runner 同源技术。

### 1.3 Anthropic 官方方法论（《Writing effective tools for agents》）

对方案最有直接指导意义的规则：

1. **响应合并塑形**：合并高频链式调用为单工具；提供 `DETAILED/CONCISE` 响应格式枚举（Slack 例：206 → 72 tokens，约 1/3）。
2. **只返回高信号字段**：`name`/`image_url` 而非 `uuid`/`mime_type`；把 UUID 解析为自然语言可降低幻觉。
3. **截断 + 分页 + 范围选择要有默认值**；"冗余调用很多"是分页/预算设错的信号。
4. **错误要可行动**：给具体的修正建议和正确输入示例，而不是错误码/堆栈。
5. **命名**：参数名无歧义（`user_id` 不是 `user`）；按服务/资源命名空间（`asana_search`）。
6. **描述写给新员工**：把隐含知识（查询格式、术语、资源关系）显式化；"对工具描述的小修"曾直接带来 SWE-bench SOTA。
7. **用 transcript 指标迭代**：追踪调用次数、非法参数错误率、冗余调用——它们分别指向描述不清、schema 不对、分页/预算失衡；甚至可以让 Claude 自己改自己的工具描述。

### 1.4 主流共识清单（后文差距分析的基准）

1. 结构化结果契约（错误/截断/元数据显式表达）
2. 输出预算与截断是默认行为，且截断时给出"如何收窄"的引导
3. 工具描述包含行为规则与反例（MUST/NEVER 级别的措辞有效）
4. 并行工具调用受支持并被引导使用
5. 精确编辑范式（先读后改、唯一性校验）或语法约束补丁
6. 计划/任务清单工具
7. 子代理作为工具，单层不嵌套
8. 上下文余量可见、可开新窗口、工具目录可延迟加载
9. 权限/审批环（人在回路）与沙箱正交组合
10. 用评测指标驱动工具描述与 schema 迭代

---

## 2. OpenAgent 现状（代码证据）

架构事实：OpenAgent 不自研工具环。循环由 MAF/M.E.AI 的 `FunctionInvokingChatClient`（FICC）承担；项目自身的贡献是 `ICapabilitySource` 发现层 + `IsolatedToolFunction` 安全包装 + 各能力源。

- 契约（`Backend/src/OpenAgent.Core/Capabilities/ICapabilitySource.cs:15-22`）：`CapabilityDefinition(Name, Description, ParametersJsonSchema, ResourceType, ResourceId, Invoke: Func<args, ct, Task<string>>, ParentResourceId)`——**结果恒为裸 string**。
- 包装（`CapabilityToolFactory.cs:99-123`）：`CapabilityAIFunction : AIFunction` 直通 string。`NormalizeSchema`（:125-141）把非法 schema **静默降级**为 `{"type":"object"}`，无日志。
- 循环配置（`Runtime/Agent/AgentFactory.cs:115-127`）：`AllowConcurrentInvocation = false`（无并行）、`MaximumIterationsPerRequest = MaxTurns>0?:5`（与文档说的默认 50 存在 fallback 漂移：未配置时实际是 5）。
- 超时隔离（本分支 `feat/tool-call-timeout`，`IsolatedToolFunction.cs:44-88`）：每工具 300s 链式 CTS，超时返回 `{"error":..., "timedOut":true}`——已对齐"单调用超时不炸整轮"的主流做法；但 `SerializeError` 会把 `exception.Message` 原样发给模型，存在内部信息泄露面。
- 工具面（全部 LLM 可见工具）：
  - 文件：`read_file`（超 1 MiB **拒绝**而非截断）、`write_file`（**只能新建**，整文件内容）、`list_files`、`create_file_transfer_url`、`compress_files`、`publish_files`、`download_file`（公共 URL→会话存储）
  - 执行：`execute_code`（Python/JS，bwrap 会话沙箱，`/input` 只读挂载、`/output` 收集，结果 JSON 含 exitCode/stdout/stderr/sandboxReset）
  - 知识：`search_knowledge_base`（人类可读编号文本，非 JSON）
  - 用户：`get_current_user_profile`
  - MCP：全部工具改名为 `mcp__{server}__{tool}`（每轮连接/销毁）
  - Skills：`load_skill`、`read_skill_resource`、`run_skill_script`（三层开关）
  - **没有**：编辑、行级读取、grep/glob、shell、计划、子代理、web 搜索/抓取、上下文查询
- 历史：`PlatformChatHistory.RepairToolHistory`（:383-466）已能把存储碎片化的并行调用折叠回单条 assistant tool-call 块——**持久层其实已为并行调用做好准备**。
- 上下文：自动压缩已实现但默认关闭（Qwen server 模板拒绝无 user message 的请求，`ConversationStoreOptions.EnableAutoCompaction = false`）。
- 审批：MAF 的 approval 回调被显式禁用（`AgentSkillsProviderFactory.cs:115-121`，理由是"当前 chat 契约无法 round-trip 审批请求"），以发现+调用两次 ACL 静态校验代替——**没有人在回路**。
- 项目自知的问题（`docs/modules/capabilities/tool-calling/README.md` Limits 节）：无并行工具调用、无结果缓存；另该文档描述的 `ExecuteToolAsync` 路由已不存在（文档漂移）。

---

## 3. 差距分析

按 §1.4 共识清单逐项打分（✅ 已对齐 / 🟡 部分 / ❌ 缺失）：

| # | 维度 | 主流基准 | OpenAgent 现状 | 差距 | 严重度 |
|---|---|---|---|---|---|
| 1 | 结果契约 | 结构化（错误/截断/元数据） | 裸 string，错误格式三种混杂（JSON/中文散文/英文前缀） | 完全缺失 | **P0** |
| 2 | 输出预算截断 | CC 25k tok 默认；Codex `max_output_tokens`=10k 且头尾保留 | 无任何工具层截断；read_file 1 MiB 直接拒绝；RAG/MCP 无界 | 完全缺失 | **P0** |
| 3 | 错误语义 | 可行动错误+正确示例 | `IncludeDetailedErrors=false` 形同虚设（IsolatedToolFunction 把异常文本发模型）；超时 JSON 与中文散文混杂 | 反向劣化 | **P0** |
| 4 | Schema 质量 | 严格、带参数描述 | 非法 schema 静默降级无日志；`required`/`additionalProperties` 不齐 | 部分 | **P0** |
| 5 | 并行调用 | 支持+引导 | 显式关闭；但持久层已支持并行历史 | 配置级差距 | **P1** |
| 6 | 编辑范式 | Edit 精确替换 / apply_patch | 只能整文件新建，不能改已有文件 | 完全缺失 | **P1** |
| 7 | 计划工具 | TodoWrite / update_plan | 无 | 完全缺失 | **P1** |
| 8 | 子代理 | Task / multi_agents | 无 | 完全缺失 | P2 |
| 9 | 上下文管理 | get_context_remaining / new_context / tool_search | 压缩已实现但默认关 | 部分 | P2 |
| 10 | 审批环 | 沙箱×审批矩阵、升级协议 | 静态 ACL，MAF 审批被禁用 | 完全缺失 | P2 |
| 11 | 执行会话 | 持久 PTY、yield+session_id+write_stdin | 会话沙箱已有（`execute_code` 复用），但工具面只有一次性 `execute_code` | 半成品（底座好、工具面未暴露） | P1 |
| 12 | 超时隔离 | per-call 超时不炸轮 | 本分支已实现 ✅ | 已对齐 | 完成 |
| 13 | 沙箱 | bwrap（Codex 同样 vendor bwrap） | bwrap 常驻会话沙箱、命名空间全套 | 已对齐甚至更严格 | — |
| 14 | MCP 生命周期 | 常驻/池化 | 每轮 connect/dispose（30s init×服务器数×每轮） | 明显劣化 | **P1** |
| 15 | 工具结果缓存 | CC WebFetch 15min 缓存 | 无 | 缺失 | P2 |
| 16 | 描述质量 | 行为契约级 | 一句话描述，无使用规则 | 明显劣化 | **P0**（改造成本最低） |

**总体判断**：OpenAgent 在"安全底座"（bwrap 沙箱、ACL、超时隔离、历史修复）上已达到甚至超过主流水准；差距集中在"模型体验面"——契约、预算、编排、编辑范式。这解释了"tool 配合很差"的体感：模型拿到的是无结构、可能超长、错误形态不一的结果，串行执行，且没有工作区把 读→改→写→跑→交付 串成闭环，只能靠整文件重写和一次性代码执行拼凑。

---

## 4. 改造方案

### 阶段 0：契约硬化（P0，预计 1-2 周，纯 `OpenAgent.Core` 内核改动）

**目标：模型立刻"看得懂"工具结果。这是全部后续工作的地基。**

1. **引入 `ToolResult` 契约**（`OpenAgent.Contracts`）：
   ```csharp
   sealed record ToolResult(
       string Content,                 // 仍是文本主体（保持 provider 无关）
       bool IsError = false,
       bool IsTruncated = false,
       string? TruncationHint = null,  // 如 "use offset=200&limit=100 to read next chunk"
       IReadOnlyDictionary<string, object?>? Metadata = null);  // exitCode、fileId、tokens…
   ```
   `CapabilityDefinition.Invoke` 签名改为 `Task<ToolResult>`；`IsolatedToolFunction`（已是唯一全工具包装点）把 `IsError` 映射到 provider 的错误通道（Anthropic `tool_result.is_error` / OpenAI 由 FICC 处理为异常或文本标记）。对旧 string 返回的适配期用隐式转换保兼容，逐源迁移。
2. **输出预算管道**（`IsolatedToolFunction` 内，对所有工具生效）：
   - 默认预算分级：读类 20k tokens / 执行日志 10k / MCP 12k（对齐 CC 25k、Codex 10k 的区间，从紧起步）。
   - 截断策略：**头尾保留、中间折叠**（`[... N chars omitted ...]`），附 `TruncationHint` 指导收窄（read_file 提示 offset/limit；execute_code 提示过滤输出）。
   - `read_file` 的"超 1 MiB 拒绝"改为"截断+提示"。
3. **统一错误格式**：`{ "error": "<what>", "code": "...", "hint": "<how to fix>" }`，全英文（模型侧）或全中文，二选一并全仓统一；`SerializeError` 停止透传 `exception.Message`，走脱敏映射表（内部异常 → 通用文案 + 日志关联 id）。
4. **Schema 硬化**：`NormalizeSchema` 降级时 `LogWarning`（含工具名），`Development` 环境直接抛错；补齐所有内置工具的 `required` / `additionalProperties:false` / 参数级 description；加一个单测清单逐工具 lint。
5. **描述重写**：按"新员工手册"标准重写内置工具描述（何时用/何时不用/失败怎么办/与其他工具的配合，如 `write_file` 明确"不能修改已有文件，要修改请…（阶段 2 前：重新生成完整内容新建）"）。
6. 顺手修两处漂移：`MaxTurns<=0` 时 fallback 5 与文档 50 不一致；`tool-calling/README.md` 中已不存在的 `ExecuteToolAsync` 描述。

**验收**：单测覆盖预算截断（头尾保留）、错误脱敏、schema lint；抓一轮真实会话 transcript，验证无 >预算 的工具结果进入历史。

### 阶段 1：执行编排（P1，预计 2-3 周）

1. **打开并行调用**：`AllowConcurrentInvocation = true`。前置审查：
   - FICC 并发调用共享 scoped `IServiceProvider` 的线程安全（逐能力源 audit：`IFileAssetService`、RagService、MCP client 的并发安全）。
   - 策略：先以**只读工具白名单**并行（`read_file`/`list_files`/`search_knowledge_base`/`get_current_user_profile`），写类（`write_file`/`publish_files`/`execute_code`）保持串行；实现方式是在 `CapabilityDefinition` 上加 `ToolConcurrency` 标注（`ReadOnly` / `Exclusive`），由 FICC 前的包装层对 Exclusive 工具做信号量串行化。
   - 持久层无需改动（`RepairToolHistory` 已支持）。
2. **`update_plan` 工具**：会话级 plan 状态（存 conversation 扩展字段），SSE 新增 `PlanUpdated` 事件供前端渲染；schema 对标 Codex（`step`+`status`，最多一个 in_progress）。系统提示引导 ≥3 步任务先建计划。
3. **MCP 连接池化**：`McpToolRuntime` 从 per-turn 生命周期改为 per-session 缓存（带健康检查与失效重连），消掉"每轮 × 每服务器 30s init"的延迟大头。

**验收**：同一请求内多个只读工具调用并发执行（日志时间戳交错）；带 plan 的任务 transcript 中模型行为改善（回归评测对比）。

### 阶段 2：工作区与编辑范式（P1，预计 3-4 周，最大架构缺口）

**目标：把"读→改→写→跑→交付"变成一等公民闭环。**

**推荐路线：以现有会话沙箱的 `/work` tmpfs 升格为"会话工作区"**（底座已在：`SessionSandboxManager` 常驻沙箱、空闲回收、`sandboxReset` 上报）。新增 4 个工具（命名前缀 `workspace_`）：

- `read_workspace_file(path, offset?, limit?)` —— `cat -n` 行号输出、默认 2000 行、截断给 hint（对标 CC Read）
- `edit_workspace_file(path, old_string, new_string, replace_all?)` —— **精确字符串替换**：必须先 read、old_string 唯一性校验、失败返回带行号上下文的可行动错误（对标 CC Edit）
- `write_workspace_file(path, content)`
- `list_workspace_files(path?, pattern?)`

**编辑范式选型论证**：选 Claude 式 str_replace 而非 Codex 式 patch 文法，因为 apply_patch 的可靠性依赖 OpenAI Responses API 的 freeform+grammar 约束解码，**跨 provider（Anthropic/Qwen/…）不可复制**；str_replace 只依赖 JSON schema，任何供应商都吃，且实现/测试成本低一个量级。

**打通闭环**：`download_file` 支持落工作区；`execute_code` 的 `/input` 除会话文件外加挂工作区（或工作区即 `/work`，天然可见）；工作区产物 → `compress_files`/`publish_files` → file asset 交付。这样 agent 可以：下载依赖 → 反复小步编辑 → 本地运行验证 → 打包交付，全程不重传整文件。

**备选路线（否决）**：在 file-asset 层直接加 `edit_file(fileId, ...)`——object storage 上的版本链"latest"语义、并发编辑、随机读写都要新造机制，且与沙箱内执行脱节；工作区路线复用已有沙箱，资产层只出现在交付两端。

**验收**：E2E——"下载 CSV → 写脚本清洗 → 编辑修 bug → 重跑 → 发布结果"全程工具调用数与 token 消耗对比现状（整文件重写路线）显著下降。

### 阶段 3：高层工具与治理（P2，按需排期）

1. **`web_fetch`**：复用 `FileAssetUrlDownloader`，加 HTML→markdown 与小模型抽取（CC 模式）；带 15min 缓存（差距 #15）。
2. **上下文管理**：`get_context_remaining` 工具；开启 auto-compaction（需先解决 Qwen 模板兼容——压缩请求补一条空 user message 或换模板即可）；工具目录超阈值（如 >40 个）时引入 `tool_search` 延迟加载 MCP 工具（对标 Codex defer_loading）。
3. **`spawn_task` 子代理**（单层，禁嵌套）：复用 `AgentFactory` 构建受限子代理（只读工具子集），结果折叠回主历史；探索型任务（"找出哪里定义了 X"）迁移到子代理以省主上下文。
4. **审批环**：扩展 chat 契约支持 round-trip——SSE 新增 `ApprovalRequired` 事件（工具名、参数摘要、风险级），新增 resume 端点回传决定；恢复 MAF approval 回调（替换当前三处 `Disable*Approval=true`）。风险标注沿用 `ToolConcurrency` 所在的元数据扩展（`RiskLevel: ReadOnly / Write / Destructive`）。首批接入：`create_file_transfer_url`（不可撤销的外发链接）、`run_skill_script`、危险 MCP 工具。
5. **评测基建**：在 Engine 侧采集 transcript 指标（每任务工具调用数、非法参数错误率、重复调用率、token 消耗），以此为回归基线驱动描述/schema 迭代（Anthropic 的 Claude-自优化-工具流程可选做）。

### 明确不建议做的

- **自造 apply_patch 文法**：如上论证，跨 provider 不可靠。
- **把代码检索 RAG 化**：Claude Code 的立场（LLM 逐层 rg/find 搜索优于 RAG 索引）在本平台同样成立——沙箱里已有 rg 可用，配 `workspace` 工具即可。
- **工具数量堆砌**：每加一个工具都过"频率 × 准确率"关（高频且专设工具能提升准确率/省 token 才加）。

---

## 5. 依赖、风险与取舍

| 风险 | 说明 | 缓解 |
|---|---|---|
| `Invoke` 签名 breaking change | 所有 CapabilityDefinition 构造点需迁移 | 隐式转换适配期 + 编译器逐源迁移；总量小（6 个内置源） |
| FICC 并行 + scoped DI | 并发下 scoped service 线程安全未知 | 只读白名单先行；Exclusive 工具信号量串行；压测 |
| 工作区 tmpfs 容量 | 32-1024 MiB，agent 产出可能超 | 配额 + `workspace_df` 提示 + 超限引导 compress 交付 |
| bwrap 无网络 vs web 工具诉求 | 工作区内无法 npm install 等 | 保持无网络（安全立场）；需要的依赖走 `download_file`；未来如开放网络须配审批环 |
| 审批环改动 chat 契约 | 涉及 Router/前端/API 三方 | 放 P2，先以 SSE 事件最小闭环验证 |
| 多 provider 描述兼容 | 同一描述在不同模型上效果不一 | 阶段 0 上线后按 transcript 指标分 provider 调 |

**不动的地方**（明确保留）：bwrap 沙箱体系、`IsolatedToolFunction` 超时语义（本分支成果）、`mcp__{server}__{tool}` 命名、file-asset 资产模型（只在工作区两端交界）。

## 6. 里程碑摘要

| 阶段 | 主交付 | 预期效果 |
|---|---|---|
| P0 契约硬化 | ToolResult、预算截断、错误统一、schema/descripiton 重写 | 工具结果可控、模型错误恢复能力提升，改动全部内核级 |
| P1 执行编排 | 并行调用、update_plan、MCP 池化 | 轮次延迟下降、长任务有结构 |
| P2 工作区 | 4 个 workspace 工具 + 交付闭环 | 编辑范式落地，token 消耗大幅下降 |
| P3 治理 | web/上下文/子代理/审批/评测 | 对齐主流全量能力 |

---

## 附：调研来源

- Claude Code 系统提示与工具描述全文提取：[wong2 gist](https://gist.github.com/wong2/e0f34aac66caf890a332f7b6f9e2ba8f)
- Claude Code 架构分析（工具分层/子代理/上下文）：[MinusX: Decoding Claude Code](https://minusx.ai/blog/decoding-claude-code)
- Anthropic 官方工具设计方法论：[Writing effective tools for AI agents](https://www.anthropic.com/engineering/writing-tools-for-agents)、[Define tools（平台文档）](https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools)
- OpenAI Codex CLI 源码（工具定义一手证据）：[openai/codex · codex-rs/core/src/tools/handlers](https://github.com/openai/codex/tree/main/codex-rs/core/src/tools/handlers)（`shell_spec.rs`、`apply_patch_spec.rs`、`plan_spec.rs`、`tool_search_spec.rs`、`get_context_remaining_spec.rs`、`multi_agents/` 等）
- Codex 沙箱与审批文档索引：[learn.chatgpt.com/docs/security](https://learn.chatgpt.com/docs/security)
- 本仓证据：`Backend/src/OpenAgent.Core/Capabilities/*`、`Runtime/Agent/AgentFactory.cs`、`IsolatedToolFunction.cs`、`PlatformChatHistory.cs`、`docs/modules/capabilities/tool-calling/README.md`
