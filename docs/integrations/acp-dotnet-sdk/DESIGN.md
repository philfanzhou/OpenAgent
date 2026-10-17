# SDK 内部架构

模块均属于 `OpenAgent.Runtime.Acp` 同一程序集；工程位置见 [DIRECTORY](./DIRECTORY.md)，行为契约见 [SPEC](./SPEC.md)。

| 模块 | 职责与依赖 |
|---|---|
| Protocol | DTO、RpcId、方法、错误、序列化、wire 校验；无 IO，不引用角色层 |
| Transport | 完整消息体与有界字节 framing，不解析 RPC/业务 |
| Rpc | 依赖 Protocol/Transport；双向请求表、dispatcher、串行 writer、取消与关闭 |
| Client / Agent | 各自依赖 Rpc/Protocol，彼此不引用；角色 API、协商、会话与回调 |
| Processes | 依赖 Transport；Launcher、stdio、stderr、ProcessLease 与退出信息，不隐式握手 |
| Diagnostics / Extensions | 脱敏事件、ActivitySource/Meter 与可选注册适配，无强制 DI/ASP.NET/exporter |

消息链：reader → envelope 校验 → response 关联 pending / request 独立分派 / notification 有序路由 → writer。长 handler 和反向请求不占用 reader；同 session 更新先入队再完成轮次，早到更新有界缓存。失败统一回收连接任务，具体配额及取消见 SPEC。

| 拥有者 | 资源及结束规则 |
|---|---|
| Connection | transport、pending、调度任务、协商结果；统一关闭，Dispose 幂等，LeaveOpen 明确流归属 |
| Client Session / Turn | 更新队列、早到通知、活动轮次；按 SPEC 有界取消/关闭，不杀共享进程 |
| Agent Host | 活跃 session、执行与 Context；关闭取消任务，历史由宿主持久化 |
| 交互注册表 | 权限/elicitation；原 connection/session 关联，一次性响应，关闭即取消 |
| 宿主 handler | 文件策略、terminalId 和执行资源；按 session 授权/回收，无 handler 不公布能力 |
| ProcessLease | 自己启动/拥有的进程、stderr 与退出结果；不随单 session 结束 |

Protocol/Rpc/Transport 不引用上层，不新增 Common 大目录；实现优先 internal，生成文件与手写 converter 分离。RPC 引擎已在 ACP-02 经同组 wire 探针对比后定为自研引擎（`Rpc/` 模块，`AcpRpcConnection`），StreamJsonRpc 仅保留为测试工程的对比基线；依据与实测矩阵见 [ADR-0005](../../decisions/0005-Acp-Rpc-Engine-Selection.md)。

控制台 → SDK，测试 → SDK + 控制台；SDK 不引用平台或测试资产。生成器是开发工具，SDK 运行时不下载 schema。固定原件/lock/许可位于 `schema/`；CodeGen 验证哈希、meta 与 x-method/x-side 后生成独立目录。DTO 持有独立 JsonElement，以生成的具体 converter 和 context 读写，手写校验器只解释固定 schema 使用的词汇；标准对象未知根字段兼容读取后丢弃，显式开放 JSON 保留。联合按已知 wire tag 优先匹配，错误 payload 不回退为默认分支。每步同步实现、测试、公共 API 控制台场景和文档，顺序见 [IMPLEMENTATION](./IMPLEMENTATION.md)。
