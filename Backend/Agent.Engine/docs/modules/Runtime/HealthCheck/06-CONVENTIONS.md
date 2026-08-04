# HealthCheck - 编码约定

## 命名约定

### 类命名

- 健康检查类后缀 `HealthCheck`：`RedisHealthCheck`、`ConfigHealthCheck`、`LlmHealthCheck`
- 位于 `Redis` 命名空间下（因为依赖 `IRedisConnectionProvider`）

### 检查名称

- 使用小写短横线格式：`redis`、`agent-config`、`llm-connectivity`
- 与 ASP.NET Core 健康检查注册名称一致

## 健康状态约定

### 状态语义

| 状态 | 含义 | 使用场景 |
|------|------|---------|
| Healthy | 功能完全正常 | Redis 连接正常、配置完整、LLM 配置可读 |
| Degraded | 功能降级但仍可用 | Redis 不可用（孤岛模式）、配置部分缓存、无已发布 Agent |
| Unhealthy | 功能不可用 | Redis Ping 失败、配置缓存为空、LLM 配置缺失 |

### 状态决策原则

1. **Redis 不可用 = Degraded**：Engine 可在无 Redis 时运行，不是致命问题
2. **配置缓存为空 = Unhealthy**：无任何配置意味着 Engine 无法处理请求
3. **LLM 配置缺失 = Unhealthy**：无 LLM 配置意味着 Agent 无法执行
4. **异常处理策略不一致**：
   - ConfigHealthCheck 异常 → Unhealthy（配置检查失败是严重问题）
   - LlmHealthCheck 异常 → Degraded（可能是临时问题）

## Description 消息约定

### 格式

- 使用完整英文句子或短语
- 包含具体数据：缓存比例、样本 Agent ID、ApiFormat、ModelId

### 示例

| 检查 | 状态 | Description |
|------|------|------------|
| Redis | Degraded | `"Redis connection not available - running in fallback mode"` |
| Redis | Healthy | `"Redis connection is healthy"` |
| Redis | Unhealthy | `"Redis connection failed"` |
| Config | Healthy | `"Config snapshot fully populated. 3/3 agents cached."` |
| Config | Degraded | `"Config snapshot partially populated. 2/3 agents cached. Sample agent: 'agent-a'."` |
| Config | Unhealthy | `"Config snapshot is empty. 0/3 agents cached in snapshot."` |
| LLM | Healthy | `"ApiFormat: OpenAICompatible, Model: gpt-4o (verified via agent 'agent-a')"` |
| LLM | Unhealthy | `"No LLM configuration available for agent 'agent-a'."` |
| LLM | Degraded | `"Unable to retrieve LLM configuration."` |

## 日志约定

### LlmHealthCheck 日志

| 场景 | 级别 | 示例 |
|------|------|------|
| 检查失败 | Warning | `"LLM health check failed"` |

> RedisHealthCheck 和 ConfigHealthCheck 不记录日志（无 ILogger 依赖）。

## 依赖注入约定

### 构造函数依赖

| 检查类 | 依赖 |
|--------|------|
| RedisHealthCheck | `IRedisConnectionProvider` |
| ConfigHealthCheck | `ConfigSnapshot`、`IRedisConnectionProvider` |
| LlmHealthCheck | `IAgentConfigProvider`、`IRedisConnectionProvider`、`ILogger<LlmHealthCheck>` |

### 注册方式

```csharp
services.AddHealthChecks()
    .AddCheck<THealthCheck>(name, tags: new[] { ... });
```

- 使用 `AddCheck<T>` 泛型注册，运行时自动从 DI 容器解析依赖
- 标签使用字符串数组

## 标签约定

| 标签 | 含义 | 检查项 |
|------|------|--------|
| `infrastructure` | 基础设施检查 | redis |
| `ready` | 就绪探针 | redis, agent-config |
| `live` | 存活探针 | redis, llm-connectivity |

## internal 访问级别

- 所有健康检查类均为 `internal`，不对外暴露
- 通过 ASP.NET Core 健康检查框架的 `AddCheck<T>` 泛型注册，框架可访问 internal 类
