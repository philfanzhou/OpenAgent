# 部署目录

- `infrastructure/docker-compose.yml`：PostgreSQL、Redis、MinIO、Keycloak。
- `infrastructure/keycloak/realm/`：Keycloak Realm 导入配置。
- `openagent/docker-compose.yml`：Engine、Router、Chat、Nginx。
- `openagent/nginx/`：代理模板和本地证书目录。
- `openagent/preview.compose.yml`：并行预览；由 `scripts/preview.sh` 管理。

在仓库根目录执行。将 `deploy/deployment.env.example` 复制为受保护的 `.env`，
两套 Compose 使用同一份环境文件：

```bash
docker compose --env-file .env -f deploy/infrastructure/docker-compose.yml --profile storage up -d
bash scripts/build-images.sh --env-file .env
bash scripts/deploy.sh --env-file .env
```

迁移已有部署时，必须把 `OPENAGENT_INFRA_PROJECT`、`OPENAGENT_COMPOSE_PROJECT` 和
`OPENAGENT_INFRA_NETWORK` 设为现有项目/网络名称，或继续使用原来的 `-p`。
目录移动不迁移数据；项目名变化会选择不同的数据卷，包括 Data Protection 密钥卷。
不执行 `down -v`，也不重建已有 Realm 来应用地址变更。

默认公网主机是 localhost：Chat 8081、Router 8082、Engine 8083、Keycloak 58081，
分别通过 HTTPS 访问。容器之间使用服务别名和内部端口；宿主端口修改不影响内部连接。
Keycloak 直接提供 HTTPS，内部 HTTP 8080 不发布到宿主机。

`OPENAGENT_CHAT_PUBLIC_URL` 同时用于 CORS、Keycloak 回调、Web Origin 和退出回调。
切换到 443 时设 `OPENAGENT_CHAT_PORT=443`、`OPENAGENT_CHAT_PUBLIC_URL=https://localhost`；
域名替换时同时更新公开 URL。Keycloak 的公开 URL 必须匹配其 HTTPS 对外入口，
Engine/Router 使用同一值作为 Authority。已有 Realm 的 Client 地址需通过管理台更新，
启动导入文件不是已有配置的更新机制。

默认证书为 `deploy/openagent/nginx/certs/tls.crt` 和 `tls.key`，两套服务共用。
自定义时给 `OPENAGENT_TLS_CERT_DIR` 设置绝对路径。证书和私钥不应进入 Git 或镜像。
生产环境另行配置密码；示例不包含实际部署域名、私钥、API key 或服务器目录。

对象存储只配置应用支持的 `OPENAGENT_S3_SERVICE_URL`，不提供无效的 PublicServiceUrl。
需要对外预签名链接时，该地址必须对链接消费者可达；本次不新增双 endpoint 签名逻辑
或自动公开 MinIO 代理。默认保持 TLS 校验，不通过关闭校验修复域名/证书问题。
