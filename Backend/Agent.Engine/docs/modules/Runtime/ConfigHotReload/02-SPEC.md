# ConfigHotReload - 功能规格说明

## 功能需求 (FR)

### FR-HR-001: Redis Pub/Sub 订阅

- **当前频道**: `agent:config:updates`（常量 `CurrentUpdatesChannel`）
- **遗留频道**: `agent:config:changed`、`skill:registry:changed`、`llm:registry:changed`、`rag:registry:changed`、`engine:config:changed`
- **行为**: `ExecuteAsync` 中订阅所有频道，然后 `Task.Delay(Timeout.Infinite)` 等待取消

### FR-HR-002: 结构化消息处理

- **消息格式**: JSON，包含 AgentId、Type、ConfigType、Version、Timestamp、Data (JsonElement?)
- **ConfigUpdate / IncrementalUpdate 类型**: 统一从 Redis 全量刷新 `agent:config:{agentId}`，由 `FullConfigRefresher` 调用 `SetFullConfig`
- **FullSync 类型**: 清空整个快照（`_snapshot.Clear()`），不再保留旧配置
- **刷新未命中**: 全量刷新时若 Redis 配置键已删除，`FullConfigRefresher` 调用 `ConfigSnapshot.Evict(agentId)` 立即移除该 agent 的全部子配置

### FR-HR-003: TTL 自愈

- **行为**: `ConfigSnapshot` 使用 `IMemoryCache` + `ConfigSnapshotOptions.AbsoluteExpirationMinutes`（默认 5 分钟）绝对过期
- **目的**: 丢失 pub/sub 消息后，过期配置不会永久缓存；下次命中再从 Redis 加载最新值
- **校验**: `AbsoluteExpirationMinutes <= 0` 时构造函数抛出 `ArgumentOutOfRangeException`，阻止立即过期的无效配置

### FR-HR-004: 已删除的能力

以下能力已在批次一移除，不再存在：

- `IConfigUpdateHandler` / `ConfigUpdateHandlerBase` / 六种 per-ConfigType handler（`FullAgentConfigHandler`、`LlmSettingsHandler`、`RagSettingsHandler`、`McpSettingsHandler`、`SkillsSettingsHandler`、`AgentSettingsHandler`）
- `ConfigVersionComparer` 版本比较与 `ConfigSnapshot.GetVersion` / 版本跟踪
- `PatchFullConfig` 增量 patch 语义

### FR-HR-006: 遗留消息处理

- **非 JSON 消息**: 交给 `LegacyMessageHandler`
- **`agent:config:changed` 频道**: 将 payload 视为 agentId，从 Redis 全量刷新
- **`agent:config:updates` 频道非 JSON**: 同样从 Redis 全量刷新
- **其他遗留频道**: 仅记录日志，不修改 Snapshot

### FR-HR-007: JSON 检测

- **方法**: `ConfigUpdateDispatcher.LooksLikeJson(string payload)` — 消息以 `{` 或 `[` 开头视为 JSON
- **非 JSON**: 走遗留消息处理流程

## 验收标准 (AC)

### AC-HR-001: 结构化 ConfigUpdate 全量刷新

- **Given** Redis 中存在 `agent:config:agent-a` 的配置
- **When** 收到 `agent:config:updates` 频道的 `{"agentId":"agent-a","type":"ConfigUpdate"}` 消息
- **Then** 从 Redis 读取全量配置，更新 Snapshot

### AC-HR-002: 结构化 IncrementalUpdate 同样触发全量刷新

- **Given** Snapshot 中存在 agent-b 的配置
- **When** 收到 `{"agentId":"agent-b","type":"IncrementalUpdate","configType":"LLMSettings","version":9,"data":{...}}` 消息
- **Then** 忽略 payload 中的子配置 data，从 Redis 全量刷新 agent-b 的完整配置

### AC-HR-003: 全量刷新在 Redis 键删除时逐出快照

- **Given** Snapshot 中存在 agent-x 的配置，但 Redis 中 `agent:config:agent-x` 已删除
- **When** 收到任意结构化更新消息指向 agent-x
- **Then** `FullConfigRefresher` 未命中，调用 `ConfigSnapshot.Evict("agent-x")` 移除全部子配置

### AC-HR-004: 遗留消息触发全量刷新

- **Given** Redis 中存在 `agent:config:agent-a` 的配置
- **When** 收到 `agent:config:changed` 频道的纯文本消息 `"agent-a"`
- **Then** 从 Redis 全量刷新 agent-a 的配置

### AC-HR-005: 遗留注册频道不修改 Snapshot

- **Given** Snapshot 中存在 agent-f 的配置
- **When** 收到 `skill:registry:changed` 频道的消息
- **Then** 仅记录日志，不修改 Snapshot

### AC-HR-006: 空消息忽略

- **Given** 任意频道
- **When** 收到空白消息
- **Then** 忽略，不修改 Snapshot

### AC-HR-007: 无效 JSON 不覆盖现有配置

- **Given** Snapshot 中存在 agent-e 的 LLMSettings
- **When** 收到无效 JSON 消息 `"{ invalid json"`
- **Then** 保留原有配置不变

### AC-HR-008: FullSync 清空快照

- **Given** 任意状态
- **When** 收到 `{"type":"FullSync"}` 消息（无需 AgentId）
- **Then** 调用 `_snapshot.Clear()` 清空整个快照

### AC-HR-009: TTL 绝对过期自动清除

- **Given** Snapshot 中 agent-ttl 的 LLMSettings 已缓存
- **When** 超过 `AbsoluteExpirationMinutes` 未收到任何更新
- **Then** 缓存条目自动过期，下次访问从 Redis 重新加载

### AC-HR-010: 非法 TTL 在构造时拒绝

- **Given** `AbsoluteExpirationMinutes = 0` 或负数
- **When** 构造 `ConfigSnapshot`
- **Then** 抛出 `ArgumentOutOfRangeException`，参数名 `ConfigSnapshot:AbsoluteExpirationMinutes`

## ConfigUpdate 消息结构

```json
{
  "agentId": "string",
  "type": "ConfigUpdate | IncrementalUpdate | FullSync",
  "configType": "FullAgentConfig | AgentSettings | LLM | LLMSettings | RAG | RAGSettings | MCP | MCPSettings | Skills | SkillsSettings",
  "version": 0,
  "timestamp": "2026-01-01T00:00:00Z",
  "data": {}
}
```
