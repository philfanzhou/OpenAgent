# GracefulShutdown - 任务清单

```json
[
  {
    "id": "GS-001",
    "title": "实现 ShutdownService 请求注册",
    "description": "RegisterRequest 生成 8 位 GUID，创建 InFlightRequest 加入 ConcurrentDictionary，停机时抛出 AgentException",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-002",
    "title": "实现 ShutdownService 请求完成",
    "description": "CompleteRequest 从字典移除请求，计算持续时间",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-003",
    "title": "实现 ShutdownAsync 等待逻辑",
    "description": "设置停机标志，轮询等待进行中请求完成，超时记录 Warning",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-004",
    "title": "实现 ShutdownService BackgroundService 生命周期",
    "description": "ExecuteAsync 等待 _shutdownSemaphore，ShutdownAsync 释放信号量",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-005",
    "title": "实现 RequestScope IDisposable 包装",
    "description": "构造时 RegisterRequest，Dispose 时 CompleteRequest，防重复释放",
    "status": "implemented",
    "file": "src/Engine/Services/RequestScope.cs"
  },
  {
    "id": "GS-006",
    "title": "DI 注册",
    "description": "ShutdownService 注册为 Singleton + HostedService",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "GS-007",
    "title": "停机编排",
    "description": "Program.cs ApplicationStopping 注册 ShutdownAsync → DeregisterAsync",
    "status": "implemented",
    "file": "src/Host/Program.cs"
  },
  {
    "id": "GS-008",
    "title": "编写 GracefulShutdown 单元测试",
    "description": "覆盖请求注册/完成、停机拒绝、超时等待、RequestScope 等场景",
    "status": "pending",
    "file": ""
  }
]
```
