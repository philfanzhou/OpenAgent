# 测试分层

仓库中的默认 `dotnet test Backend/OpenAgent.sln` 不需要 Docker、PostgreSQL、Redis、MinIO 或其他外部服务。

依赖真实 PostgreSQL/Redis 容器的集成测试已移除。单元测试、契约测试、路由测试、Engine 测试和进程内 HTTP 测试不应读取本机服务地址，也不应依赖现有 Docker 数据。

## OpenCode ACP 运行时

默认套件覆盖平台协调、事件合并、环境策略和 loopback MCP 认证，不需要安装 OpenCode。在干净服务账户/一次性测试容器中，设置 `OPENAGENT_TEST_OPENCODE` 为 OpenCode 可执行文件的绝对路径后（镜像固定版本见 `Backend/opencode.version`，其他版本单独验收），额外运行真实进程 + 本地确定性模型的测试（Runtime.Tests 与 Engine.Tests/Hosting/OpenCodeSseTests）；未设置时显式 Skip。这些测试不读取真实模型凭据，不访问付费模型，也不等价于部署环境验收。


N02 canary 对照与隔离反例位于 `OpenCodeIsolationTests`，仅创建临时技能/配置/文件；不会使用真实用户凭据。`OPENCODE_TEST_HOME` 仅在单个测试子进程中模拟忽略 HOME 的系统 profile，生产驱动不设置此开关。支持模式、Linux/Windows 验证差异及最终多租户 OS 隔离要求见 [运行时边界](../../docs/integrations/opencode-runtime.md#n02宿主继承缓解与受支持模式)。Unix 权限/符号链接测试在 Windows 明确 Skip；Windows 原生账户、ACL/junction 与进程回收仍需专门验收。
