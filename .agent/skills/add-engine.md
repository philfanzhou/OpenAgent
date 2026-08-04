# 添加 Agent Engine

## 用途

新增 LLM 执行框架或独立 `IAgentEngine` 实现时使用。仅新增 Provider Profile 时应改用
`add-llm-provider.md`。

## 工作流

1. 在 `Agent.Core/src/<EngineName>/` 创建独立项目并引用 Core/Contracts 的最低必要边界。
2. 实现 `IAgentEngine` 的普通和流式完成方法，并准确声明 `FrameworkType` 与能力支持。
3. 如需新增框架值，在 `EngineFrameworkType` 中增加兼容值，并同步序列化测试。
4. 通过 `IAgentEngineFactory` 注册；扩展方法必须使用 `TryAdd*` 或等价逻辑保证重复调用幂等。
5. 使用 `[LoggerMessage]` 源生成日志，为新事件分配稳定且不冲突的 EventId。
6. 先写引擎单元测试，再补 Factory/DI 注册测试和必要的 TestFramework 集成测试。
7. 更新 `docs/modules/engine/`、LLM 集成文档和根文档导航。

## 验证

```bash
dotnet test Backend/OpenAgent/Agent.Core/OpenAgent.Core.sln
dotnet test TestCode/TestEnv.sln
```

检查重复调用注册扩展不会产生多个 Factory，流式方法会渐进产出内容，取消令牌能向下游传播。
