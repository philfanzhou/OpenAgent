# ACP SDK 一手资料索引

> 调研日期：2026-10-15。源码链接固定到本次读取的提交；官网链接会更新。包版本若仅来自源码，不代表已经发布。机器可读 SHA/哈希及方法目录见 [sources.json](./sources.json)。

## 官方协议与专题

| 资料 | 用途 |
|---|---|
| [官网入口](https://agentclientprotocol.com/get-started/introduction) | 定位协议与角色 |
| [架构](https://agentclientprotocol.com/get-started/architecture) | 实现时核对该专题的规范与能力门控 |
| [初始化与能力](https://agentclientprotocol.com/protocol/v1/initialization) | 实现时核对该专题的规范与能力门控 |
| [会话建立/回放/恢复/关闭](https://agentclientprotocol.com/protocol/v1/session-setup) | 实现时核对该专题的规范与能力门控 |
| [轮次和取消](https://agentclientprotocol.com/protocol/v1/prompt-turn) | 实现时核对该专题的规范与能力门控 |
| [通用请求取消](https://agentclientprotocol.com/protocol/v1/cancellation) | 实现时核对该专题的规范与能力门控 |
| [内容](https://agentclientprotocol.com/protocol/v1/content) | 实现时核对该专题的规范与能力门控 |
| [工具与权限](https://agentclientprotocol.com/protocol/v1/tool-calls) | 实现时核对该专题的规范与能力门控 |
| [文件](https://agentclientprotocol.com/protocol/v1/file-system) | 实现时核对该专题的规范与能力门控 |
| [终端](https://agentclientprotocol.com/protocol/v1/terminals) | 实现时核对该专题的规范与能力门控 |
| [结构化用户输入](https://agentclientprotocol.com/protocol/v1/elicitation) | 实现时核对该专题的规范与能力门控 |
| [模式](https://agentclientprotocol.com/protocol/v1/session-modes) | 实现时核对该专题的规范与能力门控 |
| [配置选项](https://agentclientprotocol.com/protocol/v1/session-config-options) | 实现时核对该专题的规范与能力门控 |
| [认证](https://agentclientprotocol.com/protocol/v1/authentication) | 实现时核对该专题的规范与能力门控 |
| [扩展](https://agentclientprotocol.com/protocol/v1/extensibility) | 实现时核对该专题的规范与能力门控 |
| [传输](https://agentclientprotocol.com/protocol/v1/transports) | 实现时核对该专题的规范与能力门控 |
| [v2 草案迁移](https://agentclientprotocol.com/protocol/v2/migration) | 识别破坏性变化，保持与 v1 隔离 |
| [官方库与社区库](https://agentclientprotocol.com/libraries/community) | 区分官方 SDK 与社区实现 |
| [兼容测试说明](https://agentclientprotocol.com/libraries/testing) | 理解 TCK 覆盖和限制 |

## 固定源码阅读路径

以下为 12 个仓库的实际读取基线。每组先看入口文件，再按问题读具体实现；无需按顺序读完所有源码。

### agentclientprotocol/agent-client-protocol

提交：[`1cebe1c01837`](https://github.com/agentclientprotocol/agent-client-protocol/tree/1cebe1c01837379abf34dd62b71e9c75bf140669)；提交时间：`2026-10-14T23:17:30Z`。

- [schema/v1/schema.json](https://github.com/agentclientprotocol/agent-client-protocol/blob/1cebe1c01837379abf34dd62b71e9c75bf140669/schema/v1/schema.json)
- [schema/v1/meta.json](https://github.com/agentclientprotocol/agent-client-protocol/blob/1cebe1c01837379abf34dd62b71e9c75bf140669/schema/v1/meta.json)
- [schema/v2/schema.json](https://github.com/agentclientprotocol/agent-client-protocol/blob/1cebe1c01837379abf34dd62b71e9c75bf140669/schema/v2/schema.json)
- [docs/protocol/v2/migration.mdx](https://github.com/agentclientprotocol/agent-client-protocol/blob/1cebe1c01837379abf34dd62b71e9c75bf140669/docs/protocol/v2/migration.mdx)

### agentclientprotocol/typescript-sdk

提交：[`b98857b3f034`](https://github.com/agentclientprotocol/typescript-sdk/tree/b98857b3f0342915728d7e30d8a7258311054f8f)；提交时间：`2026-10-14T20:39:49+02:00`。

- [README.md](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/README.md)
- [src/acp.ts](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/src/acp.ts)
- [src/jsonrpc.ts](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/src/jsonrpc.ts)
- [src/stream.ts](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/src/stream.ts)
- [src/schema-deserialize.ts](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/src/schema-deserialize.ts)
- [src/examples/client.ts](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/src/examples/client.ts)
- [schema/schema.json](https://github.com/agentclientprotocol/typescript-sdk/blob/b98857b3f0342915728d7e30d8a7258311054f8f/schema/schema.json)

### agentclientprotocol/rust-sdk

提交：[`f5c0eef0972b`](https://github.com/agentclientprotocol/rust-sdk/tree/f5c0eef0972bbda8e75a151aeb088642911b3568)；提交时间：`2026-10-13T16:37:25+02:00`。

- [README.md](https://github.com/agentclientprotocol/rust-sdk/blob/f5c0eef0972bbda8e75a151aeb088642911b3568/README.md)
- [Cargo.toml](https://github.com/agentclientprotocol/rust-sdk/blob/f5c0eef0972bbda8e75a151aeb088642911b3568/Cargo.toml)
- [src/agent-client-protocol/src/stdio.rs](https://github.com/agentclientprotocol/rust-sdk/blob/f5c0eef0972bbda8e75a151aeb088642911b3568/src/agent-client-protocol/src/stdio.rs)

### agentclientprotocol/python-sdk

提交：[`bdb9ac7054fb`](https://github.com/agentclientprotocol/python-sdk/tree/bdb9ac7054fb2df76a922b60b356c3c65f8a8a64)；提交时间：`2026-10-12T17:33:00+08:00`。

- [README.md](https://github.com/agentclientprotocol/python-sdk/blob/bdb9ac7054fb2df76a922b60b356c3c65f8a8a64/README.md)
- [examples/agent.py](https://github.com/agentclientprotocol/python-sdk/blob/bdb9ac7054fb2df76a922b60b356c3c65f8a8a64/examples/agent.py)
- [schema/schema.json](https://github.com/agentclientprotocol/python-sdk/blob/bdb9ac7054fb2df76a922b60b356c3c65f8a8a64/schema/schema.json)

### agentclientprotocol/java-sdk

提交：[`1099b30f3704`](https://github.com/agentclientprotocol/java-sdk/tree/1099b30f3704642e21e2070d84a60213f2b63dea)；提交时间：`2026-10-08T11:06:09+02:00`。

- [README.md](https://github.com/agentclientprotocol/java-sdk/blob/1099b30f3704642e21e2070d84a60213f2b63dea/README.md)
- [integration-testing/README.md](https://github.com/agentclientprotocol/java-sdk/blob/1099b30f3704642e21e2070d84a60213f2b63dea/integration-testing/README.md)

### agentclientprotocol/kotlin-sdk

提交：[`d4df1ecc0448`](https://github.com/agentclientprotocol/kotlin-sdk/tree/d4df1ecc044835cb0863c4532680f771963b64e6)；提交时间：`2026-10-12T17:34:48+02:00`。

- [README.md](https://github.com/agentclientprotocol/kotlin-sdk/blob/d4df1ecc044835cb0863c4532680f771963b64e6/README.md)
- [acp/src/jvmTest/kotlin/com/agentclientprotocol/client/ClientHangingSessionUpdateDeliveryTest.kt](https://github.com/agentclientprotocol/kotlin-sdk/blob/d4df1ecc044835cb0863c4532680f771963b64e6/acp/src/jvmTest/kotlin/com/agentclientprotocol/client/ClientHangingSessionUpdateDeliveryTest.kt)

### agentclientprotocol/codex-acp

提交：[`9dac81d6d0f7`](https://github.com/agentclientprotocol/codex-acp/tree/9dac81d6d0f73c7798ea87d40a178018b8d968dc)；提交时间：`2026-10-15T07:21:53+02:00`。

- [README.md](https://github.com/agentclientprotocol/codex-acp/blob/9dac81d6d0f73c7798ea87d40a178018b8d968dc/README.md)
- [src/AcpAgentRouter.ts](https://github.com/agentclientprotocol/codex-acp/blob/9dac81d6d0f73c7798ea87d40a178018b8d968dc/src/AcpAgentRouter.ts)
- [src/CodexAcpClient.ts](https://github.com/agentclientprotocol/codex-acp/blob/9dac81d6d0f73c7798ea87d40a178018b8d968dc/src/CodexAcpClient.ts)

### agentclientprotocol/claude-agent-acp

提交：[`0c8de1380fd9`](https://github.com/agentclientprotocol/claude-agent-acp/tree/0c8de1380fd990291fc51cf17c6742402efb5c94)；提交时间：`2026-10-14T23:16:29+02:00`。

- [README.md](https://github.com/agentclientprotocol/claude-agent-acp/blob/0c8de1380fd990291fc51cf17c6742402efb5c94/README.md)
- [src/acp-agent.ts](https://github.com/agentclientprotocol/claude-agent-acp/blob/0c8de1380fd990291fc51cf17c6742402efb5c94/src/acp-agent.ts)

### agentclientprotocol/acp-tck

提交：[`fa00ccdc13df`](https://github.com/agentclientprotocol/acp-tck/tree/fa00ccdc13dfaa4b288a91877c1f3e468af4aa7d)；提交时间：`2026-10-02T14:11:43+02:00`。

- [README.md](https://github.com/agentclientprotocol/acp-tck/blob/fa00ccdc13dfaa4b288a91877c1f3e468af4aa7d/README.md)
- [src/tck/v1/schema/schema.json](https://github.com/agentclientprotocol/acp-tck/blob/fa00ccdc13dfaa4b288a91877c1f3e468af4aa7d/src/tck/v1/schema/schema.json)

### MertBasar0/acp-net

提交：[`52c2405ed5fd`](https://github.com/MertBasar0/acp-net/tree/52c2405ed5fdace9a53b6bb130c85cede595d97a)；提交时间：`2026-08-10T00:53:22+03:00`。

- [README.md](https://github.com/MertBasar0/acp-net/blob/52c2405ed5fdace9a53b6bb130c85cede595d97a/README.md)

### AgenticDesktop/ACPLibrary

提交：[`ba6950be2560`](https://github.com/AgenticDesktop/ACPLibrary/tree/ba6950be2560589e5c4fe4c217dc9cb626d88382)；提交时间：`2026-09-30T17:39:55+08:00`。

- [README.md](https://github.com/AgenticDesktop/ACPLibrary/blob/ba6950be2560589e5c4fe4c217dc9cb626d88382/README.md)
- [Agentic.ACPLibrary.csproj](https://github.com/AgenticDesktop/ACPLibrary/blob/ba6950be2560589e5c4fe4c217dc9cb626d88382/Agentic.ACPLibrary.csproj)

### nuskey8/acp-csharp

提交：[`94ac17601aa0`](https://github.com/nuskey8/acp-csharp/tree/94ac17601aa024110f9ae4ed90cd2c495b98d6d9)；提交时间：`2025-11-05T17:24:06+09:00`。

- [README.md](https://github.com/nuskey8/acp-csharp/blob/94ac17601aa024110f9ae4ed90cd2c495b98d6d9/README.md)
- [src/AgentClientProtocol/AgentClientProtocol.csproj](https://github.com/nuskey8/acp-csharp/blob/94ac17601aa024110f9ae4ed90cd2c495b98d6d9/src/AgentClientProtocol/AgentClientProtocol.csproj)
- [src/AgentClientProtocol/IAcpAgent.cs](https://github.com/nuskey8/acp-csharp/blob/94ac17601aa024110f9ae4ed90cd2c495b98d6d9/src/AgentClientProtocol/IAcpAgent.cs)
- [src/AgentClientProtocol/ClientSideConnection.cs](https://github.com/nuskey8/acp-csharp/blob/94ac17601aa024110f9ae4ed90cd2c495b98d6d9/src/AgentClientProtocol/ClientSideConnection.cs)

## .NET 与基础协议

- [JSON-RPC 2.0 规范](https://www.jsonrpc.org/specification)：消息、ID、错误与 batch。
- [Microsoft NewLineDelimitedMessageHandler](https://github.com/microsoft/vs-streamjsonrpc/blob/main/src/StreamJsonRpc/NewLineDelimitedMessageHandler.cs)：RPC 库候选的换行 framing 实现；此链接未固定提交，不作为已选定依赖。
- [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)：序列化元数据与反射依赖设计。

## 基线校验

稳定 schema SHA-256：

```text
4bcd1d2051ff7ed379bd9b234fb9b09c5ab9245478698e9fab805bd9b52a33a6
```

复现时从上面固定协议提交下载 `schema/v1/schema.json` 与 `meta.json`，比对 sources.json 中的哈希。升级基线时同时更新生成模型、方法目录与兼容测试；不要只改一个 NuGet 版本号。
