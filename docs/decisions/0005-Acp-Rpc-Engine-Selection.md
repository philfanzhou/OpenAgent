# ADR-0005：ACP RPC 引擎选型（自研引擎 vs StreamJsonRpc）

## 状态

已决策，第一版已实现（OPEN-9 / ACP-02）。

## 背景

ACP-02 要求同一组 wire 探针对比 StreamJsonRpc 与自有实现后确定 RPC 引擎（见 [VALIDATION](../integrations/acp-dotnet-sdk/VALIDATION.md) 行为矩阵说明），评估维度：ID/重入/取消/batch/错误/顺序、适配量/依赖/API、关闭收敛与 AOT/裁剪。约束：

- 唯一发布包 `OpenAgent.Runtime.Acp` 必须零依赖（[共同交付约束](../../planning/TODO.md)）；
- 对外 API 不暴露第三方 RPC 类型；
- 协议层要求逐方法 schema 校验（[PROTOCOL §3](../integrations/acp-dotnet-sdk/PROTOCOL.md)）、pinned STJ context、`Optional<T>`/`RpcId` 语义。

## 实测对比（同组 wire 探针，2026-10-17）

探针定义于 `Backend/tests/OpenAgent.Runtime.Acp.Tests/Rpc/EngineComparison/`，同一组场景以适配器分别驱动自研引擎（loopback 传输）与 StreamJsonRpc 2.25.29（`NewLineDelimitedMessageHandler` + `JsonMessageFormatter`，测试专用依赖）。结果矩阵钉在 `RpcWireProbeMatrix`，断言与 ADR 同步维护：

| 探针 | 自研引擎 | StreamJsonRpc 2.25.29 |
|---|---|---|
| NDJSON 无头分帧 | 原生 | 通过（内置 `NewLineDelimitedMessageHandler`） |
| 请求内等待反向调用 | 通过 | 通过 |
| int64 ID 原样回显 | 通过（不经过 double） | 通过（`RequestId` 支持 int64） |
| 未知方法 -32601 | 通过 | 通过 |
| JSON-RPC batch | 通过（混合/纯通知/空 batch/批内取消） | **不支持**（无 batch 实现） |
| `$/cancel_request`（`requestId` 参数） | 通过 | **行为不符**（监听自身取消约定，不识别 ACP 形状） |
| 关闭收敛 | 通过（pending 以 `AcpConnectionClosedException` 收敛） | 通过 |

适配量与依赖评估：

- 依赖：StreamJsonRpc 传递引入 Microsoft.VisualStudio.Threading、Nerdbank.Streams、MessagePack、Newtonsoft.Json 等，直接违反“唯一发布包零依赖”。即使只作为内部引擎引用，NuGet 依赖图仍会进入消费者。
- 协议贴合：ACP 的逐方法 schema 校验、pinned context、`result` 逐字保留 + 引擎级 `ResponseSchema` 校验、`$/cancel_request` 形状、空 batch/批内取消语义都需要在库外再包一层协议引擎；适配代码量与自研引擎核心相当，且多出一层第三方语义。
- AOT/裁剪：默认 `JsonMessageFormatter` 基于 Newtonsoft 反射序列化，AOT/裁剪不友好；自研引擎全程 source-generated STJ context。
- 行为确定性：StreamJsonRpc 的错误码、取消与关闭行为由库决定；本 SDK 的测试与验收都要求确定性（V02/V03/V05/V10/V12/V18），自研引擎可直接钉定。

## 决策

采用**自研 RPC 引擎**（`OpenAgent.Runtime.Acp.Rpc` 命名空间，`AcpRpcConnection` 及配套类型）：

- 持续 reader + 独立入站 dispatcher + 出站 pending 表 + 串行 writer；
- request/notification/batch、标准错误、ext 方法显式注册（`AcpMethod<TRequest,TResponse>` 泛型扩展 + raw JsonElement 逃生口）；
- `$/cancel_request` 按协议定义处理（读循环即拦截，不排队）；取消宽限耗尽抛 `AcpCancellationUnconfirmedException` 并关闭连接；
- 配额：`MaxPendingRequests`（本地快速失败）、`MaxConcurrentHandlers`（超限回 -32603）、`MaxQueuedNotifications`（溢出显式故障连接）；
- 对外只暴露 SDK 自有类型；StreamJsonRpc 仅存在于测试工程作为对比基线。

StreamJsonRpc 依赖仅加入 `Directory.Packages.props`（集中版本 + MessagePack 传递钉定 2.5.307）供测试工程引用；SDK 工程无任何包引用，发布包零依赖由 Release pack 检查复核。

## 后果

- 协议细节（batch、取消、错误码、逐方法校验）由 SDK 自己负责并测试，覆盖 45 信封夹具与 P1 回归门不回退。
- P3 角色 API（Client/Agent 连接、会话层）直接构建在该引擎之上；引擎 API 已在测试控制台公共 API 路径验证。
- 若未来出现零依赖且协议贴合的成熟实现，可在 `Rpc` 模块边界内重新评估；对外 API 不绑定第三方类型使替换成本可控。
