# Streaming — 任务清单 (TASKS)

> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "ExecuteStreamAsync 基本流式输出（逐 chunk yield return）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "内容片段持续产出"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式 ToolCall 合并（MergeToolCalls + EnsureToolCallIds）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "流式 chunk 中的 ToolCall 片段正确合并"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式工具调用中间状态输出（[Calling tool: X]）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "工具调用前输出中间状态文本"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式取消处理（partial 消息写回 + Cancelled 状态）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "取消时写回 partial 消息，状态为 Cancelled"
  },
  {
    "id": "TASK-05",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式失败处理（partial 消息写回 + Failed 状态 + rethrow）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "失败时写回 partial 消息，状态为 Failed，异常重新抛出"
  },
  {
    "id": "TASK-06",
    "status": "implemented",
    "depends_on": [],
    "action": "Pipeline 流式路径（中间件链 + ExecuteCoreStreamAsync）",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "流式路径经过完整中间件链"
  },
  {
    "id": "TASK-07",
    "status": "implemented",
    "depends_on": [],
    "action": "中间件 InvokeStreamAsync 接口与 WithCancellation 传播",
    "files": ["src/Core/Abstract/IAgentPipeline.cs"],
    "acceptance": "取消信号通过中间件链正确传播"
  }
]
```
