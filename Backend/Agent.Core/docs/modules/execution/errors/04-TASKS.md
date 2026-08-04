# Errors — 任务清单 (TASKS)

> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "AgentErrorCode 枚举定义（10 大类 30+ 错误码）",
    "files": ["Agent.Contracts/Requests/AgentErrorCode.cs"],
    "acceptance": "错误码覆盖权限/Skill/MCP/RAG/LLM/租户/受众/审批/请求/系统"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "AgentException 基类及 4 个子类定义",
    "files": ["Agent.Contracts/Security/Exceptions.cs"],
    "acceptance": "异常类携带 ErrorCode/Details 及特定字段"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "Pipeline 异常捕获与 AgentResponse 转换",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "AgentException 和通用异常被捕获并转为失败响应"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": [],
    "action": "Service 工具执行异常处理（AgentException 向上抛出，其他返回错误文本）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "工具执行异常不中断推理循环"
  },
  {
    "id": "TASK-05",
    "status": "implemented",
    "depends_on": [],
    "action": "Service 非流式取消/失败写回",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "取消/失败时写回已产生消息并标记状态"
  },
  {
    "id": "TASK-06",
    "status": "implemented",
    "depends_on": [],
    "action": "Service 流式取消/失败写回（terminalException 模式 + ExceptionDispatchInfo）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "流式取消/失败时写回 partial 消息并重新抛出异常"
  },
  {
    "id": "TASK-07",
    "status": "implemented",
    "depends_on": [],
    "action": "Auth 中间件抛出 AgentException(PermissionDenied)",
    "files": ["src/Core/Security/Auth.cs"],
    "acceptance": "未认证用户触发 PermissionDenied 错误"
  },
  {
    "id": "TASK-08",
    "status": "implemented",
    "depends_on": [],
    "action": "TenantValidation 中间件抛出 TenantDataIsolationException",
    "files": ["src/Core/Security/TenantValidation.cs"],
    "acceptance": "租户隔离违反触发 TenantDataIsolationViolation 错误"
  }
]
```
