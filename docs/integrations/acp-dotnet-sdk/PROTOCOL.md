# 稳定 ACP v1 协议覆盖规格

> 对应 [SPEC.md](./SPEC.md)。基线是固定提交的 `schema/v1/schema.json` + `meta.json`，不是官方 SDK 中所有 experimental 接口。以下协议事实链接到官网，SDK 加严策略明确标注。

## 1. 方法覆盖矩阵

Client 是使用 Agent 的宿主；Agent 是提供编码/任务能力的一方。表中 R 为 request/response，N 为 notification。25 个方法的机器目录见 [sources.json](./sources.json)。

### Client → Agent

| 方法 | 类型 | 可用条件与行为 | 稳定 SDK 要求 |
|---|---|---|---|
| `initialize` | R | 连接开始，协商版本、能力和认证方法 | 完整支持，存储协商结果 |
| `authenticate` | R | 使用 agent 类型的已公布 auth method；缺失 type 视为 agent | 不假设存在通用 username/password 参数 |
| `logout` | R | `agentCapabilities.auth.logout` 存在 | 只显式调用；不在测试中注销用户真实账户 |
| `session/new` | R | 完成初始化；必要时先认证 | 返回唯一 sessionId；cwd 为绝对路径；v1 mcpServers 按 schema 提供 |
| `session/load` | R | `agentCapabilities.loadSession=true` | 先回放历史 update，再响应 |
| `session/resume` | R | `agentCapabilities.sessionCapabilities.resume` 存在 | 恢复上下文，响应前不回放历史 |
| `session/list` | R | `agentCapabilities.sessionCapabilities.list` 存在 | 分页 cursor 不透明；不修改 Agent 自身的列表排序 |
| `session/close` | R | `agentCapabilities.sessionCapabilities.close` 存在 | 取消当前工作并释放活动资源；不等同删除历史 |
| `session/delete` | R | `agentCapabilities.sessionCapabilities.delete` 存在 | 从列表移除；不存在/已删应静默成功；不保证删除所有外部底层数据 |
| `session/set_mode` | R | session 公布 modes，modeId 属于 availableModes | 可在活动轮次中调用，不被 prompt 锁阻塞 |
| `session/set_config_option` | R | session 公布 configOptions | 校验 configId/value，接收更新后的配置 |
| `session/prompt` | R | session 就绪，内容类型满足协商 | 处理中持续接 update/反向请求；结果包含 stopReason |
| `session/cancel` | N | session 正有待取消轮次 | 无独立响应；最终 prompt 响应确认取消 |

来源：[初始化](https://agentclientprotocol.com/protocol/v1/initialization)、[认证](https://agentclientprotocol.com/protocol/v1/authentication)、[会话](https://agentclientprotocol.com/protocol/v1/session-setup)、[列表](https://agentclientprotocol.com/protocol/v1/session-list)、[删除](https://agentclientprotocol.com/protocol/v1/session-delete)、[配置](https://agentclientprotocol.com/protocol/v1/session-config-options)。

### Agent → Client

| 方法 | 类型 | 可用条件与行为 | 稳定 SDK 要求 |
|---|---|---|---|
| `session/update` | N | 基础会话输出；具体分支可能需能力 | 按 session 路由，保留完整原始结构 |
| `session/request_permission` | R | 基础反向请求；不依赖 fs/terminal 是否开启 | 选择有效 optionId，或 outcome cancelled |
| `fs/read_text_file` | R | `clientCapabilities.fs.readTextFile=true` | path 绝对；line 为 1 起点；limit 是行数 |
| `fs/write_text_file` | R | `clientCapabilities.fs.writeTextFile=true` | 写入完整内容；不存在时创建，授权由宿主决定 |
| `terminal/create` | R | `clientCapabilities.terminal=true` | 返回 terminalId，命令参数不经不必要的 shell 拼接 |
| `terminal/output` | R | 同上 | 立即取当前输出及可用退出信息，不等待退出 |
| `terminal/wait_for_exit` | R | 同上 | 可取消等待，不阻塞其他 RPC |
| `terminal/kill` | R | 同上 | 终止命令但保留 terminalId 和输出可读性 |
| `terminal/release` | R | 同上 | 回收资源，未退出的命令需终止；释放后 ID 无效 |
| `elicitation/create` | R | Client 明确公布请求所需 form/url 模式 | 接受/拒绝/取消；表单 schema 校验和 URL 同意由宿主实现 |
| `elicitation/complete` | N | URL elicitation 的关联完成通知 | wire 只有 elicitationId 与可选 _meta；用本地状态校验原 connection/session 归属，忽略无法关联事件 |

来源：[工具与权限](https://agentclientprotocol.com/protocol/v1/tool-calls)、[文件](https://agentclientprotocol.com/protocol/v1/file-system)、[终端](https://agentclientprotocol.com/protocol/v1/terminals)、[elicitation](https://agentclientprotocol.com/protocol/v1/elicitation)。

### 双向通用通知

| 方法 | 类型 | 要求 |
|---|---|---|
| `$/cancel_request` | N | params.requestId 指向原 request；协作取消可不支持；支持方仍回复原请求，可能是正常结果或 -32800；不替代 session/cancel |

以下为实际 wire 形状，注意通用取消参数叫 `requestId`：

```json
{"jsonrpc":"2.0","method":"$/cancel_request","params":{"requestId":"rpc-42"}}
```

会话取消则以 sessionId 定位，不能使用 requestId 代替：

```json
{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"sess-1"}}
```

## 2. 握手与能力规则

- `[协议]` Client 提出其最新支持的协议主版本；Agent 支持则返回相同版本，否则返回自己最新支持版本。Client 无法支持该版本时应关闭连接。
- `[SDK]` 首版只提出/接受 v1，不把“未来协议默认降为 v1 DTO 解析”当兼容策略。
- `[协议]` 缺失能力视为不支持；能力标记可能是布尔，也可能是存在的对象，不能统一强制反序列化成 bool。
- `[SDK]` 发送前校验对端能力，不符合时本地抛 `AcpCapabilityException`，不先发送再试错。接收未注册 request 返回 -32601；notification 不回响应。
- `[协议]` `elicitation:{}` 不代表支持 form；只有非 null 的 form/url 成员才公布相应模式。
- `[协议]` `clientCapabilities.session.configOptions.boolean:{}` 表示支持布尔配置；omitted/null 表示未公布。
- `[协议]` `clientCapabilities.session.notices:{}` 和 `.compaction:{}` 分别启用对应分支；未公布不得发送。
- `[协议]` additionalDirectories 只在 Agent 公布 `sessionCapabilities.additionalDirectories` 后发送。new/load/resume 都重新提供本次完整根列表，不自动沿用历史授权根。
- `[协议]` MCP stdio 连接属于 Agent 基础要求；MCP HTTP/SSE 由 mcpCapabilities 控制。**MCP 的 HTTP 配置不代表 ACP 连接使用 HTTP。**
- `[SDK]` 协议 SDK 只提供 MCP 连接 DTO；要发布实际 ACP Agent，handler/适配器必须实现协议要求，不能凭 SDK 声称工具后端已就绪。

## 3. 模型与更新

### ContentBlock

| 分支 | v1 prompt 支持条件 |
|---|---|
| text、resource_link | 基础要求 |
| image | promptCapabilities.image |
| audio | promptCapabilities.audio |
| resource（嵌入文本/二进制资源） | promptCapabilities.embeddedContext |

SDK 不将资源链接自动下载为本地文件，不把不同 MIME/二进制内容都转换成字符串文本。保留 annotations、URI、MIME 和 `_meta`；不对 opaque ID 和 URI 做路径标准化。[内容](https://agentclientprotocol.com/protocol/v1/content)。

### SessionUpdate 全集

稳定基线的 14 种分支：

| 组 | sessionUpdate 值 | SDK 行为 |
|---|---|---|
| 消息 | user_message_chunk、agent_message_chunk、agent_thought_chunk | 保留 content 与可选 messageId；不能给 v1 强加必填 messageId |
| 工具 | tool_call、tool_call_update | 根据 toolCallId 建立与更新；保留局部更新，不用默认空数组误清空内容 |
| 计划 | plan | 保留 entries 的顺序、优先级与状态 |
| 命令 | available_commands_update | 提供宿主展示数据，不自动执行 slash command |
| 模式 | current_mode_update | 更新已知模式状态，不自行改变授权策略 |
| 配置 | config_option_update | 保存完整配置语义；布尔选项受能力限制 |
| 会话信息 | session_info_update | 保留标题、时间和 schema 支持字段 |
| 用量 | usage_update | 展示 Agent 报告值，不推算成平台账单 |
| 通知 | notice | 需 notices 能力；属于 live 事件，不当作历史消息默认重放 |
| 压缩 | compaction_update、compaction_summary_chunk | 需 compaction 能力；按 compactionId 关联生命周期、patch 和内容流 |

本 SDK 必须提供所有分支的 DTO、读取/发送及 handler 分派；可选状态累积器对尚未实现的分支原样交出并显式标明能力限制。不得因“只显示聊天文本”而抛弃工具、用量、压缩等信息。[轮次和更新](https://agentclientprotocol.com/protocol/v1/prompt-turn)。

### 序列化生成约束

1. 从固定稳定 schema 生成 C# 模型；从 meta/x-method 生成方法名。CI 检查生成后无漂移，并输出新增/删除/改变字段。
2. 支持 anyOf/oneOf/allOf、discriminator、默认值、nullable、整数边界与非穷举类型；不直接选择某个通用生成器的默认输出后跳过验收。
3. 正确处理 schema 的 `x-deserialize-default-on-error`：对标记字段按上游规则降级；必填字段缺失不能被宽容路径悄悄补成成功。
4. 已知 discriminator 加错误 payload 按 schema 拒绝；不能伪装为 unknown 以绕过校验。
5. `[SDK]` v1 可提供只读 Unknown/raw 事件用于前向兼容和诊断；这不是允许发送任意 v1 自定义 union。未知分支不执行、不批准、不猜测成功，发送严格按稳定 schema。
6. `_meta` 与 schema 允许的开放 JSON 保留；未知对象字段可容忍读取，但用户自定义扩展只写 `_meta`，不得写标准类型根字段。透传场景用明确的 raw envelope，不能承诺一般 DTO 往返保留所有未来根字段。
7. JsonPropertyName 使用原 wire 名；不把 snake_case 方法名自动改成 C# 名称。大小写严格校验。
8. 对区分 omitted/null/value 的字段采用 Optional/自定义转换器；按各字段 schema 处理，不能把 v2 的统一 patch 语义强加于所有 v1 字段。
9. JSON-RPC envelope 的 `params` 联合按信封 `method` 判别：校验只针对该方法的 payload schema，失败即拒绝，不得让空 `required` 的候选兜底吞掉其他方法载荷。未知方法交给 ext 变体原样保留；已知方法但方向不符显式拒绝。响应 `result` 携带的方法不在信封内，信封层只做对象结构校验并逐字保留，由 `As*` 投影按具体变体严格校验；`result` 与 `error` 同时出现一律拒绝。

## 4. JSON-RPC 与 stdio

来源：[JSON-RPC 2.0](https://www.jsonrpc.org/specification)、[ACP 传输](https://agentclientprotocol.com/protocol/v1/transports)。

- UTF-8；单行 JSON 加 LF；字符串内换行正确转义，不使用 LSP Content-Length framing。允许读取常见 CRLF。
- 一次 read 可能是半条、多条或跨 UTF-8 字符边界；必须按字节缓冲完整行再解析。stdout 污染不当作聊天内容。
- request 必须有 id；notification 必须缺失 id。显式 `id:null` 与缺失 ID 是不同情况：`[SDK]` `id:null` 视为非法请求回 -32600，小数或越界数值 ID 同样回 -32600。
- ID 保留 string/int64/null 类型；本方生成非 null 字符串 ID，不将对端 int64 经 double 造成精度损失。
- 成功响应只含 result，错误只含 error；原 ID 原样回传。迟到/重复响应不二次完成 Task；诊断后丢弃，禁止回复 response 引起循环。
- SDK 默认只发送单个消息；输入 JSON-RPC batch 按通用规范处理，通知项不回应、全通知 batch 不回空数组、空 batch 返回 Invalid Request；batch 内取消不能等所有长请求结束后才分派。batch 响应按原始项顺序聚合为一个数组，仅在全部请求项完成后写出，reader 不等待。
- 非 JSON 返回 -32700、非法请求 -32600、未知 request -32601、参数错误 -32602；可恢复错误继续读下条，超长/无边界坏流或严重 I/O 错误关闭连接。错误码映射：信封结构破坏（缺 method、notification 用作 request、`$/cancel_request` 带 id）回 -32600；已知方法方向不符或未注册回 -32601；已知方法 payload 未过 schema（含 ext 变体 params 非对象）回 -32602。
- 未知 notification 忽略并可诊断，不回错误；未知 `_` 扩展 request 只有注册后执行，否则 -32601。[扩展](https://agentclientprotocol.com/protocol/v1/extensibility)。

### 引擎分发语义（P2，`OpenAgent.Runtime.Acp.Rpc`）

- 持续 reader 只做读取/校验/路由；入站 request 每个独立任务分派，notification 经有界队列按 wire 顺序路由，出站写入经单一 writer 循环保序。handler 可重入调用对端，reader 永不等待 handler。
- `$/cancel_request` 在读循环内即拦截：取消本方 outbound pending（进入宽限等待对端最终响应）、取消运行中的入站 handler token，或诊断后丢弃。取消不排队、不等待慢 handler。
- 本方取消已写出站 request 时发送 `$/cancel_request` 并等待 `CancelGracePeriod` 内的最终响应；自然完成赢得竞态；宽限耗尽抛 `AcpCancellationUnconfirmedException` 并关闭连接，回收请求表，不报告假结果。写前取消只失败本地调用，不上 wire。
- 入站 response 的 `result` 由引擎按 pending request 的方法选择 `ResponseSchema` 做严格逐方法校验（PROTOCOL §6 的 P2 接入点）；失败使该调用以本地 JsonException 失败，连接继续。本方 handler 返回的结果同样先过方法 schema 再上 wire，不发送畸形协议。
- handler 与通知 handler 收到的 `params`、调用方拿到的响应 `result` 与错误 `data` 都是引擎在帧边界克隆的 owned payload：帧的 JsonDocument 释放后仍然有效（§6 语义），handler 与通知分派不共享帧生命周期。
- 配额（产品策略）：`MaxPendingRequests`（出站，超限本地 `AcpQuotaExceededException`）、`MaxConcurrentHandlers`（入站，超限回 -32603 附 `data.limit`）、`MaxQueuedNotifications`（溢出显式故障连接）。配额耗尽不阻塞响应、取消或关闭。
- 关闭统一收敛：EOF/Dispose/故障使全部 pending 以 `AcpConnectionClosedException` 失败；终端传输/清理故障由 `Completion` 保留，干净 EOF 或 Dispose 正常完成；Dispose 幂等且不重抛终端故障。所有引擎任务都被生命周期观察，无未观察任务。

## 5. 结果、错误与资源规则

| 类型 | wire 值/语义 | SDK 映射 |
|---|---|---|
| 轮次完成 | end_turn、max_tokens、max_turn_requests、refusal、cancelled | 保留 StopReason；refusal/max_tokens 不是 RPC 故障 |
| JSON-RPC 标准 | -32700、-32600、-32601、-32602、-32603 | AcpRpcException 保留原始 code/message/data |
| 通用取消 | -32800 | 普通请求取消；不替代 prompt 的 cancelled stop reason |
| 认证缺失 | -32000 | 暴露认证需要，不自动反复重试 |
| 资源不存在 | -32002 | 保留远端信息，宿主决定是否重新创建 |
| SDK Agent 自定义错误 | 拟定 10001 SessionBusy、10002 ResourceLimit | 只用于自己的 Agent host，data 带 SDK 命名空间；不能期待所有 Agent 返回此值 |
| 本地异常 | 校验、能力、EOF、超时、进程退出、未确认取消 | 不伪装成远端返回码；允许宿主区分执行结果是否确定 |

权限 outcome 是 selected/cancelled；selected 的 optionId 必须来自请求 options。PermissionOption.kind 是选项意图，SDK 不自行把 allow_always 存成跨租户永久授权。

terminal outputByteLimit 按字节限制并在字符边界从头截断，保留 truncation 标记；kill 不 release。`terminal/release`、session close、connection shutdown 都要覆盖资源清理测试。[终端](https://agentclientprotocol.com/protocol/v1/terminals)。

## 6. 模型生成与公共入口

固定原件为 `schema/v1/schema.json`、`meta.json` 与 Apache-2.0 `LICENSE`；`schema/lock.json` 和 sources.json 共同锁定来源，原件不作格式化。生成器不联网，不在 SDK 编译时运行，不引用或打包进 SDK。生成目录只能由生成器更新；既有 CI 的 ACP 测试两次运行 CodeGen --check，在内存中重新生成并逐字节核对全部已提交文件；手工 verify.py 另验证两次写出及篡改拒绝。

```sh
dotnet run --project Backend/tools/OpenAgent.Runtime.Acp.CodeGen -- .
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.CodeGen -- . --check
python3 Backend/tools/OpenAgent.Runtime.Acp.CodeGen/verify.py . codegen-verification.json
```

verify.py 默认使用已构建 Debug 生成器，可在最后传 `Release`；报告包含生成前/第一次/第二次逐文件 SHA256 和新增/删除/变化文件。任一 schema/meta/许可/lock/方法清单不一致均失败；不更新来源或转向 latest。

```csharp
using OpenAgent.Runtime.Acp.Protocol;

ContentBlock text = new(new ContentBlockText("Hello"));
PromptRequest prompt = new("session-1", [text]);
string json = AcpJson.Serialize(prompt);
PromptRequest read = AcpJson.Deserialize<PromptRequest>(json);
string body = read.Prompt[0].AsText().Text;
```

对象以不可变 owned JSON 保存；强类型构造立即严格校验。`As*` 校验对应联合分支，投错分支抛 JsonException。每次集合 getter 返回独立值；原输入 JsonDocument 释放后 DTO、`_meta`、开放 JSON 和错误 data 仍有效。`Json` 是只读诊断视图，不自动保留标准对象的未知根字段；schema 明确开放的扩展/未来 elicitation 分支保留 raw，不能据此当作已知成功动作。

可选字段返回 `Optional<T>`；`IsDefined`、`IsNull`、`HasValue` 分别判断缺失、显式 null 和值，只有 HasValue 时可取 Value。构造时用 `Optional<T>.Null` 写 null，用默认 Optional 省略；空数组不会补成缺失。声明了 schema default 的字段另有 `*OrDefault`，不在 wire 中注入默认值。`RpcId` 是 string/int64/null 值类型；缺失 ID 由 Optional 表示，数值不经过 double。schema 标量别名映射到 CLR 基础类型；闭合/开放枚举提供 wire 常量，使用目录中的 AcpSchemaDescriptor 可单独校验别名约束。

读取仅对标记字段启用 default-on-error：非法 nullable 可选字段变 null，非法非 nullable 可选字段恢复缺省并经默认 getter 取 schema default；标记的集合跳过非法项。显式 null 落在非 nullable 的可选标记字段上同样按缺失归一（`IsDefined=false`），只有 nullable 字段保留 `IsNull`；这是 `Optional<T>` 的例外，见其文档注释。必填字段缺失、null 或类型错误不被恢复。发送/构造不启用这些宽容路径。字段大小写精确，重复 JSON 键拒绝；扩展应写入 `_meta`。数值检查固定格式与边界，JSON Schema format URI/日期作为注解，不自动规范化或访问资源；路径、权限 optionId 关联、能力门控和会话状态属于后续角色/handler 阶段。

`AcpProtocol.Schemas` 提供全部 178 个稳定定义的 schema 名、CLR 类型和 JsonTypeInfo，Methods 提供 25 个方法的方向、request/notification 及参数/结果 schema，SessionUpdates 列出 14 个 tag。目录只表示模型覆盖，不能用于宣称这些方法已可调用。JSON-RPC envelope 的执行、batch/取消分派和角色状态在 ACP-02 起实现。

运行时嵌入的 schema 是"固定 178 个上游定义 + 生成器合成定义"（当前共 323 个）：合成定义为信封联合的候选分支（`ClientRequestParams*`、`AgentResponseResultResult*` 等）及开放变体，是固定定义的确定性 allOf 合并；`AuthMethod` 的 agent 候选按 §3.4/§5 由生成器钉上 `type:"agent"` 常量，未知 discriminator 显式拒绝。公共目录只暴露 178 个固定定义——信封载荷的权威校验入口是方法自己的定义或信封模型本身，合成分支不进目录。

信封读取语义：`ClientRequest`/`AgentRequest`/`ClientNotification`/`AgentNotification` 按带内 `method` 从 `x-method` 联合中选择唯一候选并按读取宽容度校验；响应信封（`ClientResponse`/`AgentResponse`）没有带内方法，`result` 逐字保留（必须为 JSON 对象），类型化访问走 `As*()` 严格校验或 P2 的方法级引擎；`result`/`error` 互斥。合成联合单独反序列化时是原样视图（保留全部字段、拒绝重复键与非对象），不按候选归一化，也不宜作为发送前的唯一校验。信封构造路径（类型化叶子模型、`As*()` 投影、目录描述符）始终严格校验。
