# Pipeline — 测试计划 (TESTS)

测试工具：xUnit + Moq
现有测试文件：test/OpenAgent.Core.Tests/Pipeline/PipelineExecutionTests.cs

## 单元测试

### UT-01 Pipeline 正确执行中间件链

- **Given**：注册了多个中间件
- **When**：调用 ExecuteAsync
- **Then**：中间件按注册顺序执行

### UT-02 Pipeline 捕获 AgentException

- **Given**：Service 抛出 AgentException
- **When**：调用 ExecuteAsync
- **Then**：返回 AgentResponse { Success=false, ErrorCode=ex.ErrorCode }

### UT-03 Pipeline 捕获非 AgentException

- **Given**：Service 抛出 InvalidOperationException
- **When**：调用 ExecuteAsync
- **Then**：返回 AgentResponse { Success=false, ErrorCode=InternalError }

## 遗漏的测试场景

- 流式执行中间件链测试
- CancellationToken 取消传播测试
- 空中间件列表时的直接执行测试
