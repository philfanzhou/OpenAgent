# PR-1 统一 ChatRequest.Context 解析

- 分支：`refactor/unify-conversation-id-parsing`（基于 `main@9b694ab`）
- 状态：已实施
- 日期：2026-09-21

## 1. 动机

`ChatRequest.Context`（`Dictionary<string, object>?`，定义于 `Backend/src/OpenAgent.Contracts/Requests/ChatContracts.cs:7`）的解析逻辑在 Router 与 Engine.Host 各自实现了一份，且语义不一致：

| 位置 | 键匹配 | 值校验 |
|------|--------|--------|
| Router `Backend/src/OpenAgent.Router/Endpoints/ChatRequestReader.cs:70-89`（`ReadContextString`） | `Equals(key, StringComparison.OrdinalIgnoreCase)` **忽略大小写** | 严格：仅接受 JsonElement string/null、CLR string，其他抛 `JsonException` |
| Engine.Host `Backend/src/OpenAgent.Engine.Host/Extensions/AgentEndpointRequestMapper.cs:56-68`（`ReadContextValue`） | `TryGetValue` **精确匹配（大小写敏感）** | 宽松：非字符串一律 `ToString()` |

由此产生一个会话分裂 bug：客户端发送大小写不同的 context 键（如 `"ConversationId"`）时，Router 能解析并按该会话路由，Engine 却因 `TryGetValue` 精确匹配解析不到，转而回退到请求头或 `Guid.NewGuid()` 生成新会话 ID，同一逻辑会话被拆成两条。

此外 `AgentEndpointRequestMapper.IsReservedChatContextKey`（原 :83-89）单独维护了一份保留键列表，与各处硬编码的 `"conversationId"`/`"agentId"`/`"llmProfileId"` 字符串字面量（如 `RouterCachePolicy.cs:70`、`OpenAgentEngineProvider.cs:177-178`）缺乏单一事实源。

## 2. 改动清单

| 文件 | 改动 |
|------|------|
| `Backend/src/OpenAgent.Hosting/ChatRequestContext.cs` | 新建共享解析器：六个保留键常量（`ConversationIdKey`/`AgentIdKey`/`LlmProfileIdKey`/`ConversationTypeKey`/`ClientTypeKey`/`TraceIdKey`，值为 camelCase）；`ReadString(Dictionary<string, object>?, string)`（参数类型与 `ChatRequest.Context` 声明完全一致；OrdinalIgnoreCase 查找；值语义采用 Router 的严格语义）；`IsReservedKey(string)`（忽略大小写） |
| `Backend/src/OpenAgent.Router/Endpoints/ChatRequestReader.cs` | 删除私有 `ReadContextString`，`ToParsedRequest` 三处改调 `ChatRequestContext.ReadString`；form 分支与 JSON 分支的键字面量改用常量 |
| `Backend/src/OpenAgent.Engine.Host/Extensions/AgentEndpointRequestMapper.cs` | 删除 `ReadContextValue` 与 `IsReservedChatContextKey`，全部改调 `ChatRequestContext.ReadString`/`IsReservedKey`；`ReadContextEnum` 基于共享 `ReadString`（参数类型改为 `Dictionary<string, object>?`）；移除不再需要的 `using System.Text.Json` |
| `Backend/src/OpenAgent.Router/Middleware/RouterCachePolicy.cs` | 缓存策略中的 `"agentId"` 字面量改用 `ChatRequestContext.AgentIdKey` |
| `Backend/src/OpenAgent.Router/Providers/OpenAgentEngineProvider.cs` | 意图识别请求的 context 字典键 `"agentId"`/`"llmProfileId"` 改用常量 |
| `Backend/tests/OpenAgent.Hosting.Tests/ChatRequestContextTests.cs` | 新建：`ReadString`（null context、缺失键、大小写变体、JsonElement string/null/object/array/number/bool、CLR string/null/数字、异常消息断言）与 `IsReservedKey` 的 Theory+InlineData 全覆盖 |
| `Backend/tests/OpenAgent.Engine.Tests/Hosting/EndpointExtensionsTests.cs` | 新增回归用例 `CreateAgentRequest_CaseVariantContextKeys_ReadsBodyValues`：PascalCase 键可被解析，且保留键（任意大小写）不进入 `ExternalContext` |

未改动与 `ChatRequest.Context` 无关的代码：`GatewayProxyHandler`（路由值）、`GinaProvider`（外部 Provider JSON）、`ProblemDetailsFactory`（ProblemDetails 扩展字段）。

## 3. 行为变化说明

- **Router：零变化。** 共享 `ReadString` 逐行搬运自原 `ReadContextString`（忽略大小写 + 严格值校验 + 相同的 `JsonException` 消息），现有 Router 测试（含 `ReadAsync_InvalidJsonShape_ThrowsJsonException`、`ReadAsync_JsonChatContract_ReadsContextAndRewindsBody` 的 PascalCase 键用例）全部原样通过。
- **Engine.Host：键匹配从大小写敏感 → 忽略大小写；值校验从宽松（`ToString()`）→ 严格（非字符串抛 `JsonException`）。** 修复了大小写变体键导致的会话分裂。严格化是安全的：生产链路上请求先经 Router（`AgentSelectionFilter` → `ChatRequestReader.ReadAsync`），非法值类型在 Router 即以 400 拦截，不会到达 Engine；`conversationType`/`clientType` 本就是字符串枚举名，严格语义兼容。
- **`ReadContextEnum` 的容错回退不变**：无法解析的枚举值仍回退到默认值（`ConversationType.User`/`ClientType.Web`），仅改为通过共享 `ReadString` 取值。

## 4. 测试证据

`dotnet build Backend/OpenAgent.sln`：0 错误（5 个警告均为既有测试项目警告，与本次改动无关）。

`dotnet test Backend/OpenAgent.sln`：

```
已通过! - 失败: 0，通过:  56，已跳过:  0 - OpenAgent.Contracts.Tests   （基线 15，+41）
已通过! - 失败: 0，通过:   6，已跳过:  0 - OpenAgent.Architecture.Tests（基线 6）
已通过! - 失败: 0，通过:  31，已跳过: 20 - OpenAgent.Runner.Tests     （基线 31/20）
已通过! - 失败: 0，通过:  16，已跳过:  0 - OpenAgent.Infrastructure.Tests（基线 16）
已通过! - 失败: 0，通过: 103，已跳过:  0 - OpenAgent.Engine.Tests    （基线 102，+1）
已通过! - 失败: 0，通过: 285，已跳过:  2 - OpenAgent.Core.Tests      （基线 285/2）
已通过! - 失败: 0，通过: 124，已跳过:  0 - OpenAgent.Router.Tests    （基线 124）
已通过! - 失败: 0，通过:  59，已跳过:  0 - OpenAgent.Hosting.Tests   （基线 59）
```

合计 680 通过 / 0 失败 / 22 跳过（基线 638/0/22，只增不减）。

## 5. 风险与回滚

- 风险：直连 Engine.Host（不经 Router）且以非字符串类型（如数字）发送 context 保留键的客户端，原先会被静默 `ToString()`，现在得到 400。此类用法不在任何受支持客户端路径中，属预期收紧。
- 风险：忽略大小写后，同一请求中若存在多个仅大小写不同的键（如 `conversationId` 与 `ConversationId`），取字典枚举顺序的第一个；与 Router 原行为一致，无新增风险。
- 回滚：本 PR 为单 commit 纯重构 + 语义统一，`git revert <commit>` 即可完整回滚，无数据/契约变更。
