# Channels 开发

## 用途

修改 Teams、Outlook、Cron、Playground 或共享 Channel Pipeline 时使用。

## 硬性约束

- 中间件顺序固定为：异常处理 `50`、身份上下文 `100`、关联 ID `200`、限流 `300`、审计 `350`、分发 `400`。新增中间件必须说明插入点和顺序影响。
- 通道只能通过 `IChannelRouterClient` 调用 Router，不得直接引用或调用 Core、Engine。
- HTTP 客户端必须通过 `IHttpClientFactory` 获取，不得长期持有手工创建的 `HttpClient`。
- 无法从通道负载解析 tenant 时，统一使用配置的 tenant 默认值；不得以空字符串绕过租户边界。
- Outlook 轮询默认关闭，只有显式配置后才注册或运行。
- Playground 自动化请求必须设置 `expectReplies`，并断言实际回复数量，避免把异步空响应误判为成功。
- Cron Job 禁止并发重入；每次触发独立捕获并记录异常，单次失败不能停止后续调度。
- 系统主动消息的 `MessageId` 使用 `{JobName}-{Guid:N}` 格式，不能复用入站消息 ID。
- 普通 Dispatcher 请求使用 anonymous/外部用户上下文；`TriggerSystemAsync` 只能使用受控的
  system identity，禁止把系统身份混入用户入站路径。

## 工作流

1. 阅读 `Backend/OpenAgent/Agent.Channels/docs/Agent.Channels.Design.md` 和受影响适配器。
2. 先补相应测试；涉及时间的逻辑注入 `TimeProvider`，不得用短时 `Thread.Sleep`。
3. 保持 Contracts 和 Hosting 以外的生产项目引用为零。
4. 使用 `ChannelLog` 的 `[LoggerMessage]` 方法，新增稳定 EventId。
5. 运行 `dotnet test Backend/OpenAgent/Agent.Channels/OpenAgent.Channels.sln`。

## 参考

- `Backend/OpenAgent/Agent.Channels/docs/Agent.Channels.CodeWalkthrough.md`
- `.agent/rules/coding-conventions.md`
- `.agent/skills/build-and-test.md`
