# Router 服务发现与就绪

Router 使用 Redis Engine 注册索引发现动态实例，并以静态 `RouterSettings:Routing` 作为明确的回退路径。实现位于 `Backend/src/OpenAgent.Router.{Contracts,Core,Providers,Host}/`；Engine 注册协议见 [service-registration](../engine/runtime/service-registration/)。

## 服务发现

- Engine 维护 `engine:registry:index` Set，并以带 TTL 的 `engine:registry:{engineId}` String 保存注册值。
- Router 通过 `SMEMBERS` 读取索引，再用一次 `MGET` 批量读取注册值，不执行 `KEYS`/`SCAN` 全库枚举。
- 注册值按 EngineId 排序；候选实例按 intent 过滤，再按负载、EngineId 确定性排序。
- 会话亲和使用 tenantId 与 conversationId 的组合键，候选集合变化时使用一致性哈希重新映射。
- TTL 已过期、心跳超时、JSON 无效或索引悬空的实例会被排除；悬空索引成员会尽力清理。

## 故障与告警策略

| 场景 | 行为 | 可观测性 |
|------|------|----------|
| Redis 不可用 | 默认清空动态快照并走静态路由；可配置短时使用 last-known 快照 | Warning 日志、`discovery_refresh_total{outcome="redis_error"}` |
| 注册过期/无匹配 intent | 排除实例并走静态路由；无静态路由时返回不可用 | Warning 日志、发现选择指标 |
| 下游转发失败 | 将目标隔离一段时间，后续选择下一动态实例或静态目标 | Warning 日志、转发失败与选择指标 |
| 下游 ready 失败 | ready 探测下一候选；回退可用时返回 Degraded，否则 Unhealthy | 探测计数与结构化日志 |

## 关键配置

| 配置 | 默认值 | 说明 |
|------|--------|------|
| `RouterSettings:ServiceDiscovery:RedisFailureMode` | `StaticOnly` | `StaticOnly` 或 `LastKnown` |
| `RouterSettings:ServiceDiscovery:SnapshotMaxAgeSeconds` | `15` | last-known 快照最长可用时间 |
| `RouterSettings:ServiceDiscovery:FailureQuarantineSeconds` | `30` | 下游失败后的隔离时间 |
| `RouterSettings:ServiceDiscovery:ReadinessPath` | `/ready` | Engine 下游就绪端点 |
| `RouterSettings:ServiceDiscovery:ReadinessTimeoutMs` | `2000` | 单次下游探测超时 |

## 验证

Router 测试不依赖真实 Redis；Engine readiness 使用进程内 TCP 测试下游验证。

## 职责边界

Router 只做认证后的请求转发、会话 Provider 亲和、意图识别选 Agent 和服务发现。幂等、响应缓存、限流和 Agent ACL 过滤已移除；需要时由上游网关或 Engine 承担。

## 目标架构

Router 将重新定位为面向任意下游的 Agent 网关，下游标准契约见 [downstream-contract.md](./downstream-contract.md)，决策背景见 [ADR-0004](../../decisions/0004-Router-Gateway-Repositioning.md)。本文其余内容描述当前实现。
