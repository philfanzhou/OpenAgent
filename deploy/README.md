# 部署目录

启动、环境变量、镜像更新、认证及数据卷说明统一见 [部署指南](../docs/deployment.md)。

| 入口 | 内容 |
|---|---|
| [deployment.env.example](deployment.env.example) | 共享环境变量模板 |
| [infrastructure/docker-compose.yml](infrastructure/docker-compose.yml) | PostgreSQL、Redis、MinIO、Keycloak |
| [infrastructure/keycloak/realm/](infrastructure/keycloak/realm/) | Realm 导入配置 |
| [openagent/docker-compose.yml](openagent/docker-compose.yml) | Engine、Runner、Router、Chat、Nginx |
| [openagent/nginx/](openagent/nginx/) | 代理模板与证书目录 |
| [openagent/preview.compose.yml](openagent/preview.compose.yml) | 并行预览；操作见 [预览指南](../docs/parallel-previews.md) |
