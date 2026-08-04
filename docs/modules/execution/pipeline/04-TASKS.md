# Pipeline — 任务清单 (TASKS)

> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "Pipeline 委托链构建与执行",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "中间件按注册顺序执行，核心逻辑在最后调用"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": [],
    "action": "异常转换（AgentException → AgentResponse, Exception → AgentResponse）",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "AgentException 转为对应 ErrorCode，其他异常转为 InternalError"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": [],
    "action": "流式执行委托链构建",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "流式请求正确传播 CancellationToken 和 chunks"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": [],
    "action": "BuildContext 合并 AgentRequest 和 IAgentUserContext",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "UserId, TenantId, Roles, Groups, Claims, Audience, TraceId, ConversationId, AgentId 正确传递"
  }
]
```
