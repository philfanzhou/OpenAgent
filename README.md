# OpenAgent

OpenAgent 是基于 .NET 8 和 ASP.NET Core 的多服务 Agent 平台。生产代码位于 `Backend/`。

## 模块结构

```text
OpenAgent.Engine.Host ──> OpenAgent.Engine ──> OpenAgent.Core ──> OpenAgent.Contracts
        │                     │
        ├──> OpenAgent.Infrastructure ──> OpenAgent.Core
        ├──> OpenAgent.Hosting ──> OpenAgent.Contracts
        └──> OpenAgent.Router ──> OpenAgent.Core
```

| 模块 | 职责 |
|------|------|
| `OpenAgent.Contracts` | 跨模块接口、配置与 DTO（纯契约层） |
| `OpenAgent.Core` | 执行引擎、能力（MCP/RAG/Skill）、会话抽象、安全 |
| `OpenAgent.Engine` | Agent 执行服务、注册表、健康检查、配置热重载 |
| `OpenAgent.Engine.Host` | ASP.NET Core 宿主（端点、中间件、流式传输、文件资产接入） |
| `OpenAgent.Hosting` | 共享宿主、认证、Redis 与 OpenTelemetry 注册 |
| `OpenAgent.Infrastructure` | PostgreSQL+EF Core 持久化、Redis 写穿缓存、分布式锁 |
| `OpenAgent.Router` | 网关服务（路由、意图识别、限流、租户隔离） |

## 构建与测试

```bash
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln
```

## Docker 本地部署

基础设施和实际代码镜像分开部署。基础设施 Compose 同时包含 PostgreSQL、Redis、MinIO 和 Keycloak；它只需要首次启动或基础设施变更时操作，数据卷不会随着应用镜像重复构建而被重建：

```bash
docker compose -p openagent-infrastructure \
  -f docker-compose.storage.yml \
  up -d
```

应用 Compose 只引用已构建或已拉取的镜像，绝不包含 `build` 定义；Nginx 是唯一对宿主机公开 80 和 443
的服务；Chat、Router 与 Engine 不直接映射宿主机端口，分别由 Nginx 的 8081、8082、8083 HTTPS 端口代理。
部署前请将内部 CA
签发的证书和私钥放入 `docker/nginx/certs/tls.crt` 与 `docker/nginx/certs/tls.key`，并确保用户终端信任
该 CA。证书 SAN 必须包含实际访问的内网域名。

前端与 Keycloak 的公开地址必须使用 HTTPS；OIDC PKCE 和 Web Crypto 在普通 HTTP 页面中不可用。Nginx
终止 TLS 后，Router、Engine 与 Keycloak 容器之间仍可使用内部 HTTP；浏览器可见的公开地址固定为 HTTPS，
`MetadataAddress` 使用服务端可访问的内部 discovery 地址。Chat/Router/Engine 的公开地址由
`OPENAGENT_PUBLIC_HOST` 和端口变量生成；Keycloak 公开地址独立由 `OPENAGENT_KEYCLOAK_PUBLIC_URL` 配置。
生产模式需配置
`OPENAGENT_KEYCLOAK_COMMAND` 与反向代理转发头，内置 `start-dev` 仅用于本地联调。

以下示例以 `openagent.intra.example` 为公开入口。Nginx 为 Chat、Router、Engine 分别提供 8081、8082、
8083 的 HTTPS 端口；应用容器本身不映射宿主机端口：

Engine 使用 ASP.NET Data Protection 保护 PostgreSQL/Redis 中的 LLM 和 RAG 密钥，应用 Compose 会将
`/root/.aspnet/DataProtection-Keys` 挂载到独立的 `engine-data-protection` 数据卷。该卷必须和数据库卷
一样保留；删除它会导致历史密钥无法解密，需重新录入密钥。

将变量保存为受保护且 shell 兼容的 `.env` 文件（不要提交），例如：

```dotenv
OPENAGENT_PUBLIC_HOST=openagent.intra.example
OPENAGENT_CHAT_PORT=8081
OPENAGENT_ENGINE_PORT=8083
OPENAGENT_ROUTER_PORT=8082
OPENAGENT_NGINX_SERVER_NAME=openagent.intra.example
OPENAGENT_ASPNETCORE_ENVIRONMENT=Production
OPENAGENT_SERVICE_VERSION=2026.09.01
OPENAGENT_AUTH_MODE=JwtBearer
OPENAGENT_AUTH_ENABLE_KEYCLOAK=true
OPENAGENT_AUTH_ALLOW_DEVELOPMENT_ANONYMOUS=false
OPENAGENT_AUTH_AUDIENCE=openagent-api
OPENAGENT_AUTH_CLIENT_ID=openagent-chat
OPENAGENT_AUTH_REQUIRE_HTTPS_METADATA=false
OPENAGENT_AUTH_CLOCK_SKEW_SECONDS=60
OPENAGENT_KEYCLOAK_PORT=58081
OPENAGENT_KEYCLOAK_PUBLIC_URL=https://sso.intra.example:58081
OPENAGENT_KEYCLOAK_METADATA_ADDRESS=http://keycloak:8080/realms/openagent/.well-known/openid-configuration
OPENAGENT_OTLP_ENDPOINT=https://otel-collector.intra.example:4317
OPENAGENT_INFRA_NETWORK=openagent-infrastructure
```

Compose 会把 `OPENAGENT_AUTH_*` 和 `OPENAGENT_KEYCLOAK_*` 认证变量同时注入 Engine 与 Router。需要本地使用 Basic 登录调试时，保持环境为 Development，并在 `.env` 中改为：

```dotenv
OPENAGENT_ASPNETCORE_ENVIRONMENT=Development
OPENAGENT_AUTH_MODE=Basic
OPENAGENT_AUTH_ENABLE_KEYCLOAK=false
OPENAGENT_AUTH_ALLOW_DEVELOPMENT_ANONYMOUS=false
```

Basic 模式仅允许 Development 环境，开发账号为 `admin/admin` 或 `test/test`；生产环境必须使用 `JwtBearer`。`OPENAGENT_AUTH_REQUIRE_HTTPS_METADATA`、`OPENAGENT_AUTH_CLOCK_SKEW_SECONDS`、`OPENAGENT_KEYCLOAK_PUBLIC_URL` 和 `OPENAGENT_KEYCLOAK_METADATA_ADDRESS` 分别控制 OIDC 元数据校验、时钟容差、浏览器公开 Issuer 和服务端 discovery 地址。

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

先构建镜像，再部署；构建脚本不会启动容器，部署脚本不会执行构建。也可以在 `.env` 中设置
`OPENAGENT_DOCKER_MODE=auto`、`docker` 或 `wsl-docker`，省略命令行参数。

Windows PowerShell 可使用等价的 `scripts/build-images.ps1`；未指定 `-DockerMode` 时同样自动检测：

```powershell
pwsh -File ./scripts/build-images.ps1 -EnvFile ./.env
```

需要将构建好的镜像导出为 TAR 时，可指定目录；目录不存在会自动创建，三个镜像分别导出为
`openagent-engine.tar`、`openagent-router.tar` 和 `openagent-chat.tar`：

```bash
scripts/build-images.sh --env-file .env --tar-dir ./dist/images
```

PowerShell 使用 `-TarDirectory`，也可以在环境文件中设置 `OPENAGENT_IMAGE_TAR_DIR`。WSL Docker 模式会将
仓库和导出目录转换为 WSL 正斜杠路径后再执行构建与导出。

浏览器访问 `https://openagent.intra.example:8081`；Router 与 Engine 分别使用 `https://openagent.intra.example:8082`
和 `https://openagent.intra.example:8083`。Nginx 的默认
请求体限制为 64 MB（可用 `OPENAGENT_NGINX_CLIENT_MAX_BODY_SIZE` 调整），允许通过 Router 上传文件；
后端仍会按 FileAssets 与 Skill 包自身的大小限制校验请求。

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
docker compose -p openagent-app -f docker-compose.yml down
```

Gina 是可选 Provider，后续请按实际环境手动配置 Router 的 Provider 设置。Gina 的
`GET /api/agentlist` 必须返回数组或包含 `agents`/`data` 数组的 JSON；聊天使用 `POST /api/chat`。
若 Router 日志显示 Provider 不可用，先从 Router 容器内验证 `BaseUrl`、TLS、Token 和这两个路径，
再检查 Gina 返回的 JSON 字段是否至少包含 `id`/`agent_id` 或 `agentId`。

如需清理本地基础设施及其数据卷，必须明确执行：

```bash
docker compose -p openagent-infrastructure \
  -f docker-compose.storage.yml \
  down -v
```

服务器时间必须由宿主机的 NTP/chrony/systemd-timesyncd 同步；容器的 `TZ` 只影响日志显示，不能
修复 JWT 的有效期判断。可在服务器执行 `timedatectl status`、`timedatectl timesync-status`，
确认 `System clock synchronized: yes` 后再重启应用。不要通过大幅增加 `ClockSkewSeconds` 掩盖小时级
时钟偏差；当前默认容差为 60 秒，仅用于网络抖动。

需要验证真实 OIDC 登录时，基础设施 Compose 会导入本地 Realm、SPA Client 和租户 Claim Mapper；
用户与租户组织需要在 Keycloak 管理台中手动创建，详细命令参见 [Keycloak 本地认证集成](docs/integrations/keycloak/README.md)。

默认使用 Compose 内置的 MinIO（bucket `openagent-files`）。接入外部 S3/MinIO 时，可覆盖
`OPENAGENT_S3_SERVICE_URL`、`OPENAGENT_S3_BUCKET`、`OPENAGENT_S3_ACCESS_KEY`、
`OPENAGENT_S3_SECRET_KEY`；仅在自签名证书场景显式设置
`OPENAGENT_S3_ALLOW_INSECURE_TLS=true`。

## OpenTelemetry 日志中心

OpenTelemetry Collector 由部署环境单独提供，本项目不创建、不升级也不管理 Collector 容器。将外部
Collector 的 OTLP 地址配置到 `OPENAGENT_OTLP_ENDPOINT`，该变量同时驱动 Engine 和 Router 的 Logs、
Traces 与 Metrics exporter；不配置时保留 Console 与 `/metrics` 本地出口。

```bash
# 在 .env 中设置外部 Collector 地址后，仅需重启应用
OPENAGENT_OTLP_ENDPOINT=https://otel-collector.intra.example:4317 \
scripts/deploy.sh --env-file .env
```

Nginx 访问日志以 JSON 输出到容器 stdout，并保留 `traceparent` 与 `X-Trace-Id`，便于部署侧日志中心与
OTLP 信号关联。

## 文档

- [文档中心](docs/README.md) — 总览、模块、集成、数据库、架构决策
- [.agent/](.agent/README.md) — AI 工具资源（技能、规则、指南）

## 安全

生产认证使用可配置的 OIDC/OAuth2 身份提供方与 JWT Bearer 校验，验证 issuer、audience、签名和有效期。
Basic 兼容登录严格限制在 Development；它只解析凭据，不查询用户目录，也不校验真实密码。
认证只负责建立身份，角色、Agent ACL、能力权限与租户授权由独立的服务端授权层判断。
详见 [安全设计文档](docs/modules/security/README.md)。
