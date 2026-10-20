# C# ACP SDK 架构与目录

## 范围与边界

唯一发布包为 `OpenAgent.Runtime.Acp`，包含稳定 ACP v1 的协议类型、双向 RPC、Client / Agent、Session / PromptTurn 和可选本地进程管理。测试工程不可打包。继承当前 `dev` 的 `net10.0` 和集中包版本管理；SDK 无第三方运行时依赖，也不引用平台项目。

该 SDK 不替换 Application 的 MAF 执行链，不实现 Worker、租户授权、数据库存储、模型调用或 OS 沙箱。会话持久化与审批由宿主回调提供。示例文件/终端处理器默认不启用，必须显式注册及授权。

## 目录

```text
Backend/src/OpenAgent.Runtime.Acp/
├── Protocol/     手写 record、判别联合、Optional、RpcId、STJ context、方法目录
├── Transport/    IAcpTransport、UTF-8 NDJSON framing、大小限制与所有权
├── Rpc/          请求关联、并发分派、取消、batch、错误、配额与诊断
├── Client/       AcpClient、AcpSession、AcpPromptTurn、交互回调与可选本地处理器
├── Agent/        AcpAgentHost、AcpAgentHandlers、反向调用上下文
└── Processes/    AcpProcess：命令参数、环境、stderr、退出与租约释放
Backend/tests/OpenAgent.Runtime.Acp.Tests/
├── Protocol/    契约摘要与来源哈希、联合、字段、patch、ID 与分配回归
├── Transport/   分片、CRLF、EOF、并发写入、取消与释放
├── Rpc/         关联、重入、batch、一般取消、错误与配额
├── Client/      会话、交互、文件与关闭边界
├── Processes/   真实子进程的 stderr 排空、退出码与释放击杀
├── Architecture/ SDK 依赖边界
├── Fixtures/    官方固定契约摘要与许可，仅供测试
└── TestDoubles/ 可控流、测试连接与 ProcessWorker 辅助子进程
```

## 协议类型与精简原则

精简以减少重复与额外抽象为目标，不以压缩行数为目标。SDK 与测试保留分支块、异常处理、缩进与换行，并在生命周期和协议边界处增加中文注释。

- 稳定目录包含 25 个方法（含一般取消）、14 类会话更新及五类内容。公开 DTO 以少量按领域分组的文件组织。
- 普通字段是普通 CLR 属性，访问不会重新解析 JSON 或创建替代模型。`JsonElement` 只用于 `_meta`、原始工具输入输出、开放 schema/能力字段等扩展载荷。
- STJ source context 仅声明序列化元数据，编译生成物留在 `obj/`。不提交数百个生成包装类，不在运行时嵌入 schema 或维护通用 schema 验证器。
- `Optional<T>` 仅用于补丁字段，区分省略、显式 null 与值。`RpcId` 保留字符串/整数/null 的区别，不将 `1` 和 `"1"` 混为同号。
- 所有解析结果拥有自己的 JSON 生命周期；未知字段保存在 `Extensions`。当前未知更新判别值会被忽略，不能自动投射为已知事件；升级 schema 时增加类型和契约测试。
- 原先 P1 的独立 CodeGen 工程与模型再生成任务，按本次简化要求改为手写 DTO + 固定官方 schema 契约摘要 + 编译期 STJ 元数据。输入摘要仅一份测试资料，不进入 SDK 包。

## RPC 与资源生命周期

`AcpStreamTransport` 负责单 reader 和串行完整写入。RPC reader 持续接收，响应/取消/快速通知直接路由，耗时请求进入独立 worker；不能在通知回调等待 UI。出站 pending 和入站取消表分开，允许双向使用相同 ID。batch 同步登记入站请求后并行执行，收集响应为一个数组；notification 永不产生响应。

一般请求取消使用 `$/cancel_request`，本地超时/取消及时结束等待。prompt 采用 `session/cancel`，必须继续接收尾部更新并等待原 prompt 的最终结果；超过宽限仍未确认则关闭连接并抛 `AcpCancellationUnconfirmedException`。EOF 使挂起请求和更新流失败，不能解释为 `end_turn`。

每个 session 同时只允许一个 prompt、一个更新消费者。更新归属于 session，因此 new/load 响应前的回放也能缓存；恢复无须依赖“当前 turn”。消息数/字节、并发请求、正在创建的会话及本地终端均有限额。慢消费者溢出直接报告连接失败，不静默丢事件。`ActivitySource` 和 `Meter` 记录方法、次数及耗时，不记录 prompt、文件内容或认证载荷。

Client/Agent 释放关闭连接；Session 释放只清理本会话及已协商的远端 close；`AcpProcess` 独立拥有自己启动的进程。释放进程先关闭 stdin、等待宽限，必要时结束自有进程树，并持续排空 stderr。关闭一个 session 不结束共享 Agent 进程。

## 功能接入与扩展

Client/Agent 使用 handler record，不另建 DI 框架或强制实现大接口。注册了文件、终端、form/url 或 terminal-auth 回调才发布对应能力。一般扩展在 `ConfigureConnection` 中注册；自定义模型在 AOT 下传入自己的 `JsonTypeInfo`，原始扩展使用 `JsonElement`。

权限默认 cancelled，选项必须来自请求；取消待审批操作也返回 cancelled。文件示例逐段解析符号链接、限制授权根/输入输出量，默认禁止写入；路径检查与实际打开之间仍存在 TOCTOU，宿主必须配合沙箱。终端示例要求命令授权回调，以 ArgumentList 启动且不隐式创建 shell，按 UTF-8 字节保留输出尾部；kill 保留 ID/output，release 失效并回收。

Elicitation 支持 form/url 和 accept/decline/cancel、基础字段/必填/enum/长度/数值/数组校验及连接内 URL 完成关联；不是通用 JSON Schema 引擎。宿主负责额外格式规则及 UI。认证支持默认 agent 流程和显式 terminal 流程：terminal 交给宿主交互启动并检查退出码，禁止把该类型发送到 authenticate；logout 必须协商。SDK 不保存凭据。
