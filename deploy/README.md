# 部署入口

操作、迁移与认证说明见 [部署指南](../docs/deployment.md)，变量默认值及必填项见 [完整清单](../docs/deployment-variables.md)。

| 应用 | 独立配置模板 | Docker 启动文件 |
|---|---|---|
| Engine | [engine.env.example](openagent/engine.env.example) | [engine.compose.yml](openagent/engine.compose.yml) |
| Router | [router.env.example](openagent/router.env.example) | [router.compose.yml](openagent/router.compose.yml) |
| Runner | [runner.env.example](openagent/runner.env.example) | [runner.compose.yml](openagent/runner.compose.yml) |
| Chat | [chat.env.example](openagent/chat.env.example) | [chat.compose.yml](openagent/chat.compose.yml) |
| 可选 HTTPS gateway | [gateway.env.example](openagent/gateway.env.example) | [gateway.compose.yml](openagent/gateway.compose.yml) |
| 可选 PostgreSQL | [postgres.env.example](infrastructure/postgres.env.example) | [postgres.compose.yml](infrastructure/postgres.compose.yml) |
| 可选 Redis | [redis.env.example](infrastructure/redis.env.example) | [redis.compose.yml](infrastructure/redis.compose.yml) |
| 可选 MinIO | [minio.env.example](infrastructure/minio.env.example) | [minio.compose.yml](infrastructure/minio.compose.yml) |
| 可选 Keycloak | [keycloak.env.example](infrastructure/keycloak.env.example) | [keycloak.compose.yml](infrastructure/keycloak.compose.yml) |
| 源码镜像构建 | [build.env.example](build.env.example) | [build.compose.yml](build.compose.yml) |
| 可选组合启动 | [deployment.env.example](deployment.env.example) | [应用组合](openagent/docker-compose.yml)、[基础设施组合](infrastructure/docker-compose.yml) |
| 并行开发预览 | [预览指南](../docs/parallel-previews.md) | [preview.compose.yml](openagent/preview.compose.yml) |
