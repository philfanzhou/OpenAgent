# ServiceRegistration - 编码约定

## 命名约定

### 接口命名

- 注册接口前缀 `I`：`IEngineRegistry`
- 方法使用 `Async` 后缀：`RegisterAsync`、`HeartbeatAsync`、`DeregisterAsync`
- 布尔属性使用 `Is` 前缀：`IsRegistered`

### 类命名

- Redis 实现类前缀 `Redis`：`RedisRegistry`
- 后台服务后缀 `Service`：`HeartbeatService`
- 配置模型后缀 `Options`：`HeartbeatOptions`
- 数据模型后缀 `Entry`：`RegistryEntry`

### 方法命名

- 内部辅助方法使用 `PascalCase`：`GetCurrentLoad`、`GetMemoryPressure`、`GetGCPressure`、`GetThreadPoolPressure`
- 静态工厂/检测方法：`DetectPort`
- TTL 获取方法：`GetTtl`

### 字段命名

- 私有字段使用 `_` 前缀 + camelCase：`_redis`、`_entry`、`_logger`、`_options`、`_isRegistered`、`_disposed`、`_portSet`
- 静态只读字段使用 `PascalCase`：无此场景

## 日志约定

### 日志级别

| 场景 | 级别 | 示例 |
|------|------|------|
| 注册成功 | Information | `"Engine registered with ID: {EngineId} at {Host}:{Port}"` |
| 注销成功 | Information | `"Engine deregistered from Redis. ID: {EngineId}"` |
| 心跳服务启动 | Information | `"Engine heartbeat service starting..."` |
| 心跳服务停止 | Information | `"Engine heartbeat service stopped."` |
| 端口检测完成 | Information | `"Detected listening port after app start: {Port}"` |
| 注册失败 | Warning | `"Failed to register engine in Redis. Continuing in island mode."` |
| 心跳失败 | Warning | `"Failed to send heartbeat to Redis."` |
| 注销失败 | Warning | `"Failed to deregister engine from Redis. TTL will expire naturally."` |
| StringSetAsync 返回 false | Warning | `"Failed to register engine in Redis. StringSetAsync returned false."` |
| 心跳循环异常 | Warning | `"Heartbeat failed, will retry..."` |
| 未注册尝试心跳 | Information | `"Engine not registered. Attempting to register..."` |

### 结构化日志参数

- 使用命名参数：`{EngineId}`、`{Host}`、`{Port}`
- 日志消息为完整英文句子，首字母大写

## 错误处理约定

### 异常处理策略

- **不向上抛出**：`RegisterAsync`、`HeartbeatAsync`、`DeregisterAsync` 均捕获所有异常
- **降级而非崩溃**：Redis 不可用时进入孤岛模式，不阻止 Engine 运行
- **TTL 兜底**：注销失败时依赖 TTL 自然过期，不重试

### 异常消息格式

- `"Failed to register engine in Redis. Continuing in island mode."`
- `"Failed to send heartbeat to Redis."`
- `"Failed to deregister engine from Redis. TTL will expire naturally."`

## Redis Key 命名约定

- 格式：`{领域}:{操作}:{标识符}`
- 示例：`engine:registry:{engineId}`
- 使用冒号 `:` 作为分隔符

## DI 注册约定

- 接口注册为 Singleton：`services.AddSingleton<IEngineRegistry, RedisRegistry>()`
- 后台服务使用 `AddHostedService`：`services.AddHostedService<HeartbeatService>()`
- 配置使用 `Configure<T>`：`services.Configure<HeartbeatOptions>(configuration.GetSection("Heartbeat"))`

## 配置约定

- 配置节名称与 Options 类名对应（去掉 Options 后缀）：`Heartbeat` → `HeartbeatOptions`
- 默认值在 Options 类属性初始化器中设置
- TTL 最小值保护：`Math.Max(value, 1)`
- 间隔最小值保护：`Math.Max(value, 1)`

## 并发约定

- `IsRegistered` 使用普通 `bool`（非 volatile），因为心跳循环为单线程顺序执行
- `_portSet` 使用普通 `bool`，由 `ApplicationStarted` 回调设置后由主循环读取
