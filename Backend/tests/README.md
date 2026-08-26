# 测试分层

默认的 `dotnet test Backend/OpenAgent.sln` 不需要 Docker、PostgreSQL、Redis、MinIO 或其他外部服务。

当前只有以下测试属于真实基础设施集成测试，并且默认跳过：

- `OpenAgent.Infrastructure.Tests.InfrastructurePersistenceTests`：PostgreSQL
- `OpenAgent.Infrastructure.Tests.RedisConversationLockTests`：Redis
- `OpenAgent.Router.Tests.Routing.RedisRouterIntegrationTests`：Redis

这些测试统一标记为 `Category=Container`，只有显式设置环境变量后才会启动 Testcontainers：

```bash
OPENAGENT_RUN_CONTAINER_TESTS=1 dotnet test Backend/OpenAgent.sln --filter Category=Container
```

单元测试、契约测试、路由测试、Engine 测试和进程内 HTTP 测试不应读取本机服务地址，也不应依赖现有 Docker 数据。
