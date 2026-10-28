# 测试分层

仓库中的默认 `dotnet test Backend/OpenAgent.sln` 不需要 Docker、PostgreSQL、Redis、MinIO 或其他外部服务。

依赖真实 PostgreSQL/Redis 容器的集成测试已移除。单元测试、契约测试、路由测试、Engine 测试和进程内 HTTP 测试不应读取本机服务地址，也不应依赖现有 Docker 数据。
