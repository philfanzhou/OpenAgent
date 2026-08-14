# Execution Error Handling

统一错误处理定义执行层异常分类、传播规则和会话写回保障，确保异常不吞没、不丢失上下文。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 异常分类 | `AgentException` 携带 `AgentErrorCode`（10 大类 30+ 错误码）|
| AgentExecutor 转换 | 捕获 `AgentException` / 通用 `Exception` → `AgentResponse(Success=false)` |
| 工具异常隔离 | 非 AgentException 返回错误文本，不中断推理循环 |
| 会话写回保障 | 取消/失败时写回 partial 消息（`CancellationToken.None`）|

## Architecture
```text
AgentExecutor 层:
  AgentException → AgentResponse(Success=false, ErrorCode, ErrorMessage)
  Exception      → AgentResponse(Success=false, InternalError)

MAF 工具执行:
  AgentException → 直接 throw
  Exception      → return "Error executing tool: ..."

会话写回取消/失败:
  先 PersistPartialAssistantMessage → 再 throw（ExceptionDispatchInfo 保留堆栈）
```

## Current Status
**Implemented** — 异常分类、AgentExecutor 转换、工具异常处理、写回保障均已落地。

## Limits
- HTTP 状态码映射属宿主层，Core 不负责
- 流式路径异常通过 `ExceptionDispatchInfo.Capture` 延迟重新抛出

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`, `Backend/src/OpenAgent.Contracts/Requests/AgentErrorCode.cs`
- Core: `Backend/src/OpenAgent.Core/Runtime/Agent/`
- Tests: 无专门测试文件（待补充）
