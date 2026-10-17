# 测试分层

仓库中的默认 `dotnet test Backend/OpenAgent.sln` 不需要 Docker、PostgreSQL、Redis、MinIO 或其他外部服务。

依赖真实 PostgreSQL/Redis 容器的集成测试已移除。单元测试、契约测试、路由测试、Engine 测试和进程内 HTTP 测试不应读取本机服务地址，也不应依赖现有 Docker 数据。

ACP 单包测试位于 `OpenAgent.Runtime.Acp.Tests`，默认仅测试 SDK 和离线测试控制台；不调用真实模型。新增 ACP 能力须同步 SDK、测试、测试控制台，分阶段任务见 [ACP 实现任务](../../docs/integrations/acp-dotnet-sdk/IMPLEMENTATION.md)。
