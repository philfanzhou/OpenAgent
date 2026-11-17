# 部署目录

初始化、启动、固定名称及迁移说明统一见 [部署指南](../docs/operations/deployment.md)。

| 入口 | 内容 |
|---|---|
| [.env.example](.env.example) | 公共部署参数；必填项启用、可选项注释 |
| [openagent/.env.example](openagent/.env.example) | Engine / Router 共用运行配置 |
| [openagent/engine/.env.example](openagent/engine/.env.example) | Engine 专用配置 |
| [openagent/router/.env.example](openagent/router/.env.example) | Router 专用配置 |
| [openagent/docker-compose.yml](openagent/docker-compose.yml) | dev 业务栈：Engine、Router、Chat |
| [nginx/](nginx/) | 优先部署的独立 Nginx 与网络定义 |
| [openagent/nginx/certs/](openagent/nginx/certs/) | 为兼容 infrastructure 保留的共享证书位置 |
| [infrastructure/docker-compose.yml](infrastructure/docker-compose.yml) | 原基础设施配置，保持不变 |
| [infrastructure/keycloak/realm/](infrastructure/keycloak/realm/) | 原 Realm 配置 |
| [openagent/preview.compose.yml](openagent/preview.compose.yml) | 开发预览，见 [预览指南](../docs/operations/parallel-previews.md) |
