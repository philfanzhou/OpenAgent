# 测试分层

仓库中的默认 `dotnet test Backend/OpenAgent.sln` 不需要 Docker、PostgreSQL、Redis、MinIO 或其他外部服务。

依赖真实 PostgreSQL/Redis 容器的集成测试已移除。单元测试、契约测试、路由测试、Engine 测试和进程内 HTTP 测试不应读取本机服务地址，也不应依赖现有 Docker 数据。

## OpenCode ACP 运行时

默认套件覆盖平台协调、事件合并、环境策略和 loopback MCP 认证，不需要安装 OpenCode。设置 `OPENAGENT_TEST_OPENCODE` 为本机 OpenCode 可执行文件的绝对路径后（版本不固定），额外运行真实进程 + 本地确定性模型的测试（Runtime.Tests 与 Engine.Tests/Hosting/OpenCodeSseTests）；未设置时显式 Skip。这些测试不读取真实模型凭据，不访问付费模型，也不等价于部署环境验收。
