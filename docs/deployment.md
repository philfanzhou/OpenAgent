# 部署指南

在仓库根目录执行以下操作。当前架构见 [系统上下文](overview/SystemContext.md)，生产容灾与权限待办见 [任务清单](planning/TODO.md)。

## 启动与更新

目录与迁移说明见 [部署目录](../deploy/README.md)，共享变量模板见
[deployment.env.example](../deploy/deployment.env.example)。已有部署必须保留原来的 Compose 项目名和数据卷。

基础设施和实际代码镜像分开部署。基础设施 Compose 同时包含 PostgreSQL、Redis、MinIO 和 Keycloak；它只需要首次启动或基础设施变更时操作，数据卷不会随着应用镜像重复构建而被重建：

```bash
docker compose -p openagent-infrastructure \
  -f deploy/infrastructure/docker-compose.yml \
  --env-file .env --profile storage up -d
```

## HTTPS 与认证配置

应用 Compose 只引用已构建或已拉取的镜像，绝不包含 `build` 定义；
Chat、Router 与 Engine 不直接映射宿主机端口，分别由 Nginx 的 8081、8082、8083 HTTPS 端口代理。
部署前请将内部 CA
签发的证书和私钥放入 `deploy/openagent/nginx/certs/tls.crt` 与 `deploy/openagent/nginx/certs/tls.key`，并确保用户终端信任
该 CA。证书 SAN 必须包含实际访问的内网域名。

前端与 Keycloak 的公开地址必须使用 HTTPS；OIDC PKCE 和 Web Crypto 在普通 HTTP 页面中不可用。Nginx
终止 TLS 后，Router、Engine 与 Keycloak 容器之间仍可使用内部 HTTP；浏览器可见的公开地址固定为 HTTPS，
`MetadataAddress` 使用服务端可访问的内部 discovery 地址。Chat/Router/Engine 的公开地址由
`OPENAGENT_PUBLIC_HOST` 和端口变量生成；Keycloak 公开地址默认同样由 `OPENAGENT_PUBLIC_HOST` 与
`OPENAGENT_KEYCLOAK_PORT` 生成，需要覆盖时设置 `OPENAGENT_KEYCLOAK_PUBLIC_URL`。
生产模式需配置
`OPENAGENT_KEYCLOAK_COMMAND` 与反向代理转发头，内置 `start-dev` 仅用于本地联调。

Engine 使用 ASP.NET Data Protection 保护 PostgreSQL/Redis 中的 LLM 和 RAG 密钥，应用 Compose 会将
`/root/.aspnet/DataProtection-Keys` 挂载到独立的 `engine-data-protection` 数据卷。该卷必须和数据库卷
一样保留；删除它会导致历史密钥无法解密，需重新录入密钥。

从 [deployment.env.example](../deploy/deployment.env.example) 复制变量到受保护且 shell 兼容的 `.env` 文件（不要提交）。模板中的凭据用于本地联调，生产环境需覆盖。

开发环境若不经过 TLS 反向代理，可将 `OPENAGENT_PUBLIC_SCHEME` 设为 `http`，并使用 Development Basic；生产环境应保持 `https`。

Compose 会把 `OPENAGENT_AUTH_*` 和 `OPENAGENT_KEYCLOAK_*` 认证变量同时注入 Engine 与 Router。需要本地使用 Basic 登录调试时，保持环境为 Development，并在 `.env` 中改为：

```dotenv
OPENAGENT_ASPNETCORE_ENVIRONMENT=Development
OPENAGENT_AUTH_MODE=Basic
OPENAGENT_AUTH_ENABLE_KEYCLOAK=false
OPENAGENT_AUTH_ALLOW_DEVELOPMENT_ANONYMOUS=false
```

Basic 模式仅允许 Development 环境，开发账号为 `admin/admin` 或 `test/test`；生产环境必须使用 `JwtBearer`。`OPENAGENT_AUTH_REQUIRE_HTTPS_METADATA`、`OPENAGENT_AUTH_CLOCK_SKEW_SECONDS`、`OPENAGENT_KEYCLOAK_PUBLIC_URL` 和 `OPENAGENT_KEYCLOAK_METADATA_ADDRESS` 分别控制 OIDC 元数据校验、时钟容差、浏览器公开 Issuer 和服务端 discovery 地址。

## 镜像构建与导出

构建脚本支持本机 Docker 和 WSL Docker。未指定模式时会自动检测当前可用的 Docker；也可以使用
`--docker-mode docker` 或 `--docker-mode wsl-docker` 强制选择。部署也使用同一模式，确保镜像位于同一个
Docker daemon：

```bash
# 本机 Docker
scripts/build-images.sh --env-file .env --docker-mode docker
scripts/deploy.sh --env-file .env --docker-mode docker

# WSL Docker（在 WSL 终端中执行）
scripts/build-images.sh --env-file .env --docker-mode wsl-docker
scripts/deploy.sh --env-file .env --docker-mode wsl-docker
```

先构建镜像，再部署；构建脚本不会启动容器，部署脚本不会执行构建。Chat 镜像的构建参数与部署环境文件
同名（`OPENAGENT_PUBLIC_SCHEME`/`OPENAGENT_PUBLIC_HOST` 与 Router/Engine 端口），浏览器端地址在镜像内派生；
不经过脚本直接 `docker build` 时也使用同一组变量。也可以在 `.env` 中设置
`OPENAGENT_DOCKER_MODE=auto`、`docker` 或 `wsl-docker`，省略命令行参数。

Windows PowerShell 可使用等价的 `scripts/build-images.ps1`；未指定 `-DockerMode` 时同样自动检测：

```powershell
pwsh -File ./scripts/build-images.ps1 -EnvFile ./.env
```

需要将构建好的镜像导出为 TAR 时，可指定目录；目录不存在会自动创建，四个镜像分别导出为
`openagent-engine.tar`、`openagent-router.tar`、`openagent-chat.tar` 和 `openagent-runner.tar`。
Runner 随应用一起部署，部署环境文件必须设置 `OPENAGENT_RUNNER_API_KEY`（至少 32 字符）；详见 [Runner 部署](integrations/code-runner.md)：

```bash
scripts/build-images.sh --env-file .env --tar-dir ./dist/images
```

PowerShell 使用 `-TarDirectory`，也可以在环境文件中设置 `OPENAGENT_IMAGE_TAR_DIR`。WSL Docker 模式会将
仓库和导出目录转换为 WSL 正斜杠路径后再执行构建与导出。

## 应用更新与排障

浏览器访问 `https://openagent.intra.example:8081`；Router 与 Engine 分别使用 `https://openagent.intra.example:8082`
和 `https://openagent.intra.example:8083`。Nginx 的默认
请求体限制为 1100 MB（可用 `OPENAGENT_NGINX_CLIENT_MAX_BODY_SIZE` 调整），允许通过 Router 上传大文件
（前端单文件上限 100 MB、单次会话附件总量 1000 MB）；后端仍会按 FileAssets 与 Skill 包自身的大小限制校验请求。

Compose 不包含任何模型凭据或已发布 Agent。Development Basic 兼容登录只适合受控联调，不应直接暴露到
内网或公网；生产环境请使用 HTTPS 的企业 IdP 与 OIDC。

镜像更新时依次执行构建与部署；仅修改运行时变量（例如 OTLP 地址）时只执行部署。三个应用镜像标签可分别由
`OPENAGENT_ENGINE_IMAGE`、`OPENAGENT_ROUTER_IMAGE` 与 `OPENAGENT_CHAT_IMAGE` 覆盖，因而也可改为私有镜像仓库
已经推送的标签：

```bash
scripts/build-images.sh --env-file .env
scripts/deploy.sh --env-file .env
```

如需停止应用，不会删除基础设施数据：

```bash
docker compose -p openagent-app -f deploy/openagent/docker-compose.yml down
```

Gina 是可选 Provider，后续请按实际环境手动配置 Router 的 Provider 设置。Gina 的
`GET /api/agentlist` 必须返回数组或包含 `agents`/`data` 数组的 JSON；聊天使用 `POST /api/chat`。
若 Router 日志显示 Provider 不可用，先从 Router 容器内验证 `BaseUrl`、TLS、Token 和这两个路径，
再检查 Gina 返回的 JSON 字段是否至少包含 `id`/`agent_id` 或 `agentId`。

如需清理本地基础设施及其数据卷，必须明确执行：

```bash
docker compose -p openagent-infrastructure \
  -f deploy/infrastructure/docker-compose.yml \
  down -v
```

## 时间、身份与对象存储

服务器时间必须由宿主机的 NTP/chrony/systemd-timesyncd 同步；容器的 `TZ` 只影响日志显示，不能
修复 JWT 的有效期判断。可在服务器执行 `timedatectl status`、`timedatectl timesync-status`，
确认 `System clock synchronized: yes` 后再重启应用。不要通过大幅增加 `ClockSkewSeconds` 掩盖小时级
时钟偏差；当前默认容差为 60 秒，仅用于网络抖动。

需要验证真实 OIDC 登录时，基础设施 Compose 会导入本地 Realm、SPA Client 和租户 Claim Mapper；
用户与租户组织需要在 Keycloak 管理台中手动创建，详细命令参见 [Keycloak 本地认证集成](integrations/keycloak/README.md)。

默认使用 Compose 内置的 MinIO（bucket `openagent-files`）。接入外部 S3/MinIO 时，可覆盖
`OPENAGENT_S3_SERVICE_URL`、`OPENAGENT_S3_BUCKET`、`OPENAGENT_S3_ACCESS_KEY`、
`OPENAGENT_S3_SECRET_KEY`；仅在自签名证书场景显式设置
`OPENAGENT_ALLOW_INSECURE_TLS=true`。

如果服务器容器没有正确安装内部 CA，可在确认所有出站地址可信的前提下设置
`OPENAGENT_ALLOW_INSECURE_TLS=true`，让 LLM、S3、MCP、RAG 及其他平台 HTTP 请求跳过 TLS 证书校验。
该选项默认关闭，生产环境优先安装并信任内部 CA；它同时覆盖平台 HTTP 客户端和 JWT/OIDC 认证回调。

## 日志中心

将外部 Collector 地址写入 `.env` 的 `OPENAGENT_OTLP_ENDPOINT` 后执行部署脚本。Exporter、信号出口和 Trace 关联规则统一见 [可观测性](overview/Observability.md)；本仓库不管理 Collector 容器。Nginx JSON 访问日志保留 `traceparent` 与 `X-Trace-Id`，供部署侧日志中心关联。

## 已有部署与访问地址

迁移已有部署时，必须把 `OPENAGENT_INFRA_PROJECT`、`OPENAGENT_COMPOSE_PROJECT` 和
`OPENAGENT_INFRA_NETWORK` 设为现有项目/网络名称，或继续使用原来的 `-p`。
目录移动不迁移数据；项目名变化会选择不同的数据卷，包括 Data Protection 密钥卷。
不执行 `down -v`，也不重建已有 Realm 来应用地址变更。

默认公网主机是 localhost：Chat 8081、Router 8082、Engine 8083、Keycloak 58081，
分别通过 HTTPS 访问。容器之间使用服务别名和内部端口；宿主端口修改不影响内部连接。
Keycloak 直接提供 HTTPS，内部 HTTP 8080 不发布到宿主机。
应用栈创建独立的 `openagent` 网络；Engine 和 Router 额外加入基础设施网络以访问数据库、Redis、MinIO 和 Keycloak，
Runner、Chat、Nginx 不直接加入基础设施网络。

`OPENAGENT_CHAT_PUBLIC_URL` 同时用于 CORS、Keycloak 回调、Web Origin 和退出回调，
默认由 `OPENAGENT_PUBLIC_SCHEME`/`OPENAGENT_PUBLIC_HOST` 与 `OPENAGENT_CHAT_PORT` 派生，无需重复配置。
切换到 443 时设 `OPENAGENT_CHAT_PORT=443`、`OPENAGENT_CHAT_PUBLIC_URL=https://localhost`；
域名替换时同时更新公开 URL。Keycloak 的公开 URL 必须匹配其 HTTPS 对外入口，
Engine/Router 使用同一值作为 Authority。已有 Realm 的 Client 地址需通过管理台更新，
启动导入文件不是已有配置的更新机制。

默认证书为 `deploy/openagent/nginx/certs/tls.crt` 和 `tls.key`，两套服务共用。
自定义时给 `OPENAGENT_TLS_CERT_DIR` 设置绝对路径。证书和私钥不应进入 Git 或镜像。
本地部署可直接使用示例中的默认凭据；生产环境应覆盖数据库、MinIO 和 Keycloak 凭据。Runner API key 仍必须单独生成，示例不包含实际部署域名、私钥、API key 或服务器目录。

对象存储使用 `OPENAGENT_S3_SERVICE_URL` 供引擎内部访问。客户端对外下载与分享使用平台端点（`/api/v1/share/{token}`、认证下载），S3 无需公网域名；底层存储实现仍保留签名能力。默认保持 TLS 校验。

## 认证边界

生产认证使用可配置的 OIDC/OAuth2 身份提供方与 JWT Bearer 校验，验证 issuer、audience、签名和有效期。
Basic 兼容登录严格限制在 Development；它只解析凭据，不查询用户目录，也不校验真实密码。
认证只负责建立身份，角色、Agent ACL、能力权限与租户授权由独立的服务端授权层判断。
详见 [安全设计文档](modules/security/README.md)。
