# Agent Review Notes

## 1. 本轮任务范围

对 Agent.Engine 项目 docs/ 目录进行二次审校和优化，主要工作包括：修正断链和旧目录引用、清理已归档的旧目录、验证模块文档与源代码的一致性。

## 2. 已完成的文档调整

### 2.1 断链修复

| 文件 | 修复内容 |
|------|----------|
| `overview/README.md` | 将 `../00-overview/` 和 `../01-runtime/` 引用替换为 `../modules/README.md` 和 `../Integration/README.md` |
| `overview/Requirements.md` | 5 处旧目录引用替换为新结构路径（FR-1→ChatApi/03-DESIGN.md, FR-2→ServiceRegistration/03-DESIGN.md, FR-3→ConfigHotReload/03-DESIGN.md, NFR-2→ErrorHandling/03-DESIGN.md, NFR-4→HealthCheck/03-DESIGN.md） |
| `overview/DotNetCodingPolicy.md` | 移除对已删除 `../99-engineering/DotNet-Coding-Policy.md` 的 2 处引用 |

> **注意**：`DotNetCodingPolicy.md` 已合并到 `.agent/rules/coding-conventions.md`。

### 2.2 旧目录清理

| 操作 | 说明 |
|------|------|
| 删除 `00-overview/` | 3 个文件（Architecture.md, Implementation.md, Requirements.md），内容已迁移至 overview/ |
| 删除 `01-runtime/` | 4 个文件（Health-Checks.md, Hot-Reload.md, Observability.md, Redis-Integration.md），内容已拆分至 modules/ 和 Integration/ |
| 删除 `99-engineering/` | 1 个文件（DotNet-Coding-Policy.md），内容已迁移至 overview/DotNetCodingPolicy.md |

> **注意**：`overview/DotNetCodingPolicy.md` 已合并到 `.agent/rules/coding-conventions.md`。

### 2.3 模块文档准确性修正

| 模块 | 修正内容 |
|------|----------|
| Host/ChatApi | 01-FEATURE.md："三种流式传输协议"→"两种"；02-SPEC.md：SSE 端点 OperationCanceledException 条件补充；06-CONVENTIONS.md：区分 NDJSON/SSE 取消条件差异 |
| Host/ErrorHandling | 02-SPEC.md/03-DESIGN.md/06-CONVENTIONS.md：未知异常 Detail 文本修正为 `"An unexpected error occurred"`；ProblemDetails Instance 字段描述按异常类型分别说明；UnauthorizedAccessException 信息暴露描述修正 |
| Runtime/ServiceRegistration | 02-SPEC.md/03-DESIGN.md：Host 解析描述修正（`Dns.GetHostName()` vs `Environment.MachineName`）；Port 解析描述补充构造时默认 0；启动注册方式修正为 `Task.Run` 异步调用 |

### 2.4 验证通过的模块（无需修改）

- Runtime/ConfigManagement
- Runtime/ConfigHotReload
- Runtime/GracefulShutdown
- Runtime/HealthCheck
- Runtime/CapabilityRegistration
- Integration/Redis

## 3. 待人工审核事项

### 3.1 Redis Pub/Sub 重连后订阅恢复

- **问题描述**：`RedisConnectionProvider` 使用 StackExchange.Redis，连接断开后重连时，已注册的 `Subscribe` 回调是否会自动恢复，还是需要重新订阅？
- **已查阅证据**：`HotReloadService` 在 `ExecuteAsync` 中调用 `_redis.Subscribe(channel, handler)`，仅调用一次；StackExchange.Redis 官方文档称 `ConnectionRestored` 事件后订阅会自动恢复，但未在代码中找到显式验证逻辑
- **仍无法完全确认原因**：需要实际断连/重连测试才能确认行为
- **建议复核方式**：在测试环境中断开 Redis 连接后恢复，观察 `HotReloadService` 是否仍能收到 Pub/Sub 消息

### 3.2 SSE 端点检测路径匹配

- **问题描述**：`SseErrorHandlerMiddleware.IsSseEndpoint()` 使用 `context.Request.Path.Value?.Contains("/sse")` 进行路径匹配，可能误匹配包含 "sse" 的非 SSE 端点
- **已查阅证据**：当前端点只有 `/api/v1/agent/chat/sse` 包含 "/sse"，但未来新增端点可能受影响
- **仍无法完全确认原因**：这是设计意图还是临时实现不明确
- **建议复核方式**：确认是否应改为精确路径匹配（如 `EndsWith("/sse")`）

## 4. 证据不足但已落盘的内容

| 内容 | 文档位置 | 标注 |
|------|----------|------|
| Redis Pub/Sub 重连后订阅自动恢复 | `overview/Integration.md` | `[待确认]` |
| SSE 中间件路径匹配可能误匹配 | `modules/Host/ErrorHandling/06-CONVENTIONS.md` | `[待确认]` |
| HeartbeatService 端口检测默认值 80 | `modules/Runtime/ServiceRegistration/03-DESIGN.md` | 代码中硬编码，非配置项 |
| RedisMockSkill 使用 IHttpClientFactory 命名客户端 "SkillEndpoint" | `modules/Runtime/CapabilityRegistration/03-DESIGN.md` | 已更新，继承 Core skip-cert handler 并尊重 DNS 刷新 |

## 5. 风险与后续建议

1. **测试覆盖缺口**：ServiceRegistration、GracefulShutdown、CapabilityRegistration 三个功能点当前无单元测试，建议优先补充
2. **Observability 文档**：旧 `01-runtime/Observability.md` 的内容在新结构中没有独立对应文档，可观测性信息分散在 overview/Integration.md 和各功能点的 06-CONVENTIONS.md 中，建议评估是否需要在 modules/ 下新增可观测性功能点
3. **database/ 目录**：Agent.Engine 不使用关系型数据库，所有外部状态存储在 Redis 中，因此未创建 database/ 目录。Redis Key 模式文档位于 `Integration/Redis/03-DESIGN.md`
4. **development/ 目录**：当前未创建 development/ 目录。如需本地启动、调试、验证等开发执行文档，建议后续按需新增

## 6. 2026-07-10 SRP 重构目录收口

- 配置读取迁移到 `src/Engine/Config/`，热更新迁移到 `src/Engine/Reload/`。
- 心跳与停机协调迁移到 `src/Engine/Runtime/`，注册与负载采集迁移到 `src/Engine/Registry/`。
- Host 仍为独立项目，项目引用与公开契约未改变。
