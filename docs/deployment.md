# 部署指南

在仓库根目录执行以下操作。当前架构见 [系统上下文](overview/SystemContext.md)，生产容灾与权限待办见 [任务清单](planning/TODO.md)。

## 启动与更新

目录与迁移说明见 [部署目录](../deploy/README.md)，必填变量模板见
[deployment.env.example](../deploy/deployment.env.example)。已有部署必须保留原来的 Compose 项目名和数据卷。

基础设施和应用镜像分开部署。基础设施 Compose 包含 PostgreSQL、Redis、MinIO 和可选 Keycloak；Basic 默认不启动 Keycloak，需要 OIDC 时额外添加 `--profile keycloak`。它只需要首次启动或基础设施变更时操作，数据卷不会随着应用镜像重复构建而被重建：

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
终止 TLS 后，Router 与 Engine 容器之间仍可使用内部 HTTP；生产 OIDC 元数据使用 HTTPS。
Docker Compose 默认公开地址为 `https://localhost`，Chat/Router/Engine 端口分别为 8081/8082/8083；需要覆盖时，按
`docker-compose.yml` 中的注释同步修改 Nginx 端口、CORS 来源和前端公开地址。Keycloak 公开地址默认
`https://localhost:58081`，外部 IdP 地址按 Compose 注释覆盖。
内置 Keycloak 固定使用 `start --import-realm`；经过外部代理时按其要求配置转发头。

Engine 使用 ASP.NET Data Protection 保护 PostgreSQL/Redis 中的 LLM 和 RAG 密钥，应用 Compose 会将
`/root/.aspnet/DataProtection-Keys` 挂载到独立的 `engine-data-protection` 数据卷。该卷必须和数据库卷
一样保留；删除它会导致历史密钥无法解密，需重新录入密钥。

从 [deployment.env.example](../deploy/deployment.env.example) 复制到受保护的 `.env` 文件，只填写 6 项必填凭据。Compose 使用 `${变量:?提示}` 检查必填项，未设置或为空时会在启动前报错，不提供默认凭据。可选覆盖项连同默认值和用途写在各自 `docker-compose.yml` 的注释中；需要调整时再取消注释并替换对应的固定默认值。

| 使用方 | 配置归属 | 默认值与必填项 |
|---|---|---|
| Engine | `OPENAGENT_POSTGRES_PASSWORD`、`OPENAGENT_S3_*` | 数据库密码、S3 Endpoint/Access Key/Secret Key 必填；数据库 `openagent`、bucket `openagent-files`、Region `us-east-1`、path-style `true` |
| Engine + Router | `OPENAGENT_AUTH_BASIC_PASSWORD` | 默认 `Basic`，用户名 `admin`，租户 `development`；密码必填；Keycloak、API Key、开发匿名均默认关闭 |
| Engine + Router | Compose 注释中的 Redis、OTLP、TLS 覆盖项 | 默认不连接 Redis/Collector，不跳过 TLS 校验；仅按部署需要启用 |
| Engine + Runner | `OPENAGENT_RUNNER_API_KEY` | 必填，至少 32 字符；Runner 不接收数据库、S3 或用户登录密码 |
| Chat / Nginx | Compose 注释中的公开地址、端口、证书目录覆盖项 | 默认 `https://localhost`，Chat/Router/Engine 分别 8081/8082/8083；Chat 不接收后端凭据 |
| 各应用 / Compose | 镜像、项目名、网络名 | 镜像 `openagent-<应用>:latest`；应用项目 `openagent-app`，基础设施项目 `openagent-infrastructure`；已有部署需保留项目名、网络名和数据卷 |
| 可选 Keycloak | `--profile keycloak` 与 Compose 中的 OIDC 注释项 | 只在选用 JwtBearer 时启动和配置；默认不启动，公开地址 `https://localhost:58081`，生产 discovery 使用可信 HTTPS |

Engine 与 Router 各自显式列出认证参数，并从同一个必填密码变量读取 Basic 凭据。配置密码后不再接受内置开发账号；匿名访问默认关闭，对外入口使用 HTTPS。

选择 Keycloak 时额外启动其 profile，并按应用 Compose 注释将 Engine 与 Router 的 `Authentication__Mode` 改为 `JwtBearer`、启用 `Authentication__EnableKeycloak`。同时按基础设施 Compose 注释填写 Keycloak 管理员凭据和公开地址。生产自定义 discovery 必须使用 HTTPS。Basic 无需这些配置。

生产匿名访问固定关闭，OIDC 元数据固定要求 HTTPS，JWT 时钟容差固定为 60 秒；这三项不再暴露为部署变量。Keycloak 内部启动命令、HTTP 开关和动态 backchannel 也使用固定默认值。开发预览继续使用其独立配置。

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

先构建镜像，再部署；构建脚本不会启动容器，部署脚本不会执行构建。默认 Chat 公开地址为
`https://localhost:8081`；自定义域名时还需在构建 Chat 镜像时提供 `OPENAGENT_PUBLIC_HOST`，并按 Compose 注释
同步修改运行时的 Nginx、CORS 与 Keycloak 地址。不经过脚本直接 `docker build` 时也使用相同构建参数。也可以在 `.env` 中设置
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
请求体限制默认为 1100 MB（需要调整时修改应用 Compose 中的 Nginx 环境项），允许通过 Router 上传大文件
（前端单文件上限 100 MB、单次会话附件总量 1000 MB）；后端仍会按 FileAssets 与 Skill 包自身的大小限制校验请求。

Compose 不包含模型凭据或已发布 Agent。生产 Basic 使用配置的账号密码；可选企业 IdP 与 OIDC。配置管理接口仍仅在 Development 映射。

镜像更新时依次执行构建与部署；仅修改运行时配置时只执行部署。默认使用本地 `latest` 镜像：

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

S3 Endpoint 和凭据必须填写，bucket 默认 `openagent-files` 且需预先存在。使用内置 MinIO 时启动 `storage` profile，Endpoint 填 `http://minio:9000`。接入其他 S3 兼容服务时，填写其 API Endpoint、Access Key、Secret Key，并按厂商要求在应用 Compose 注释中调整 Region 和 path-style（AWS 通常为 `false`）。现有实现使用 AWS SDK，兼容范围取决于对方 S3 API/SigV4 支持，需验证上传、下载及 GetBucketLocation 健康检查；未做各厂商实测。

如果服务器容器没有正确安装内部 CA，可在确认所有出站地址可信的前提下，在 Engine 和 Router Compose 注释中启用
`OPENAGENT_ALLOW_INSECURE_TLS=true`，让 LLM、S3、MCP、RAG 及其他平台 HTTP 请求跳过 TLS 证书校验。
该选项默认关闭，生产环境优先安装并信任内部 CA；它同时覆盖平台 HTTP 客户端和 JWT/OIDC 认证回调。

## 日志中心

配置外部 Collector 时，按应用 Compose 注释启用 `OpenTelemetry__OtlpEndpoint` 并填写地址。Exporter、信号出口和 Trace 关联规则统一见 [可观测性](overview/Observability.md)；本仓库不管理 Collector 容器。Nginx JSON 访问日志保留 `traceparent` 与 `X-Trace-Id`，供部署侧日志中心关联。

## 已有部署与访问地址

迁移已有部署时，必须通过 `-p` 保留原来的 Compose 项目名，并在 Compose 文件的 `name:` 与网络名处保留原名称。
目录移动不迁移数据；项目名变化会选择不同的数据卷，包括 Data Protection 密钥卷。
不执行 `down -v`，也不重建已有 Realm 来应用地址变更。

默认公网主机是 localhost：Chat 8081、Router 8082、Engine 8083、Keycloak 58081，
分别通过 HTTPS 访问。容器之间使用服务别名和内部端口；宿主端口修改不影响内部连接。
Keycloak 直接提供 HTTPS，内部 HTTP 8080 不发布到宿主机。
应用栈创建独立的 `openagent` 网络；Engine 和 Router 额外加入基础设施网络以访问数据库、Redis、MinIO 和 Keycloak，
Runner、Chat、Nginx 不直接加入基础设施网络。

Chat 公开地址同时用于 CORS、Keycloak 回调、Web Origin 和退出回调。自定义域名或端口时，
需同步更新应用 Compose 中的 CORS、Nginx 端口/主机配置、前端构建参数及 Keycloak 回调地址。
Keycloak 的公开 URL 必须匹配其 HTTPS 对外入口，
Engine/Router 使用同一值作为 Authority。已有 Realm 的 Client 地址需通过管理台更新，
启动导入文件不是已有配置的更新机制。

默认证书为 `deploy/openagent/nginx/certs/tls.crt` 和 `tls.key`，两套服务共用。
自定义时按 Compose 文件中的挂载注释修改证书目录。证书和私钥不应进入 Git 或镜像。
数据库、S3、生产 Basic 密码和 Runner Key 均不提供可用默认值，需填写；其他参数可沿用默认值。

对象存储使用 `OPENAGENT_S3_SERVICE_URL` 供引擎内部访问。客户端对外下载与分享使用平台端点（`/api/v1/share/{token}`、认证下载），S3 无需公网域名；底层存储实现仍保留签名能力。默认保持 TLS 校验。

## 认证边界

默认 Basic 校验配置的账号密码，租户取自服务端配置；可选 OIDC/JWT Bearer 验证 issuer、audience、签名和有效期。
内置开发账号只在 Development 且未配置 Basic 密码时使用，不查询外部用户目录。
认证只负责建立身份，角色、Agent ACL、能力权限与租户授权由独立的服务端授权层判断。
详见 [安全设计文档](modules/security/README.md)。
