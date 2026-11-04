# 部署指南

在仓库根目录执行以下操作。当前架构见 [系统上下文](../overview/SystemContext.md)，生产容灾与权限待办见 [任务清单](../planning/TODO.md)。

## 启动与更新

目录与迁移说明见 [部署目录](../../deploy/README.md)，必填变量模板见
[deployment.env.example](../../deploy/deployment.env.example)。已有部署必须保留原来的 Compose 项目名和数据卷。

基础设施和实际代码镜像分开部署。基础设施 Compose 包含 PostgreSQL、Redis、MinIO 和可选 Keycloak；Basic 默认不启动 Keycloak，需要 OIDC 时额外添加 `--profile keycloak`。它只需要首次启动或基础设施变更时操作，数据卷不会随着应用镜像重复构建而被重建：

```bash
docker compose -p openagent-infrastructure \
  -f deploy/infrastructure/docker-compose.yml \
  --env-file .env --profile storage up -d
```

注意镜像可得性：MinIO 官方镜像（`minio/minio` 与 `minio/mc`）已于 2026-09 从 Docker Hub 与 quay.io 删除。
固定标签仅在本机缓存或镜像加速器仍保留副本时可拉取；全新环境请按基础设施 Compose 中的注释，用
`OPENAGENT_MINIO_IMAGE` 与 `OPENAGENT_MC_IMAGE` 指向内网仓库或仍可访问的镜像源。

应用 Compose 默认设置 `Database__ApplyMigrationsOnStartup=true`：Engine 启动时自动应用待执行的 EF 迁移
（对 Production 同样生效），全新数据库无需手工建表；升级镜像时也由它滚动应用新迁移。

## HTTPS 与认证配置

应用 Compose 只引用已构建或已拉取的镜像，绝不包含 `build` 定义；
Chat、Router 与 Engine 不直接映射宿主机端口，分别由 Nginx 的 8081、8082、8083 HTTPS 端口代理。
部署前请将内部 CA
签发的证书和私钥放入 `deploy/openagent/nginx/certs/tls.crt` 与 `deploy/openagent/nginx/certs/tls.key`，并确保用户终端信任
该 CA。证书 SAN 必须包含实际访问的内网域名。

前端与 Keycloak 的公开地址必须使用 HTTPS；OIDC PKCE 和 Web Crypto 在普通 HTTP 页面中不可用。Nginx
终止 TLS 后，Router、Engine 与 Keycloak 容器之间仍可使用内部 HTTP；浏览器可见的公开地址固定为 HTTPS。
Chat/Router/Engine 默认 `https://localhost` 的 8081/8082/8083；需要更换域名或端口时按应用 Compose 中的
注释同步修改 Nginx 端口与 listen 值。Keycloak 公开地址默认 `https://localhost:58081`，外部 IdP 地址按
Compose 注释覆盖；内置 Keycloak 固定使用 `start --import-realm`，经过外部代理时按其要求配置转发头。

Engine 使用 ASP.NET Data Protection 保护 PostgreSQL/Redis 中的 LLM 和 RAG 密钥，应用 Compose 会将
`/home/app/.aspnet/DataProtection-Keys` 挂载到独立的 `engine-data-protection` 数据卷。该卷必须和数据库卷
一样保留；删除它会导致历史密钥无法解密，需重新录入密钥。

从 [deployment.env.example](../../deploy/deployment.env.example) 复制到受保护的 `.env` 文件，只填写 5 项必填凭据。Compose 使用 `${变量:?提示}` 检查必填项，未设置或为空时会在启动前报错，不提供默认凭据。可选覆盖项连同默认值和用途写在各自 `docker-compose.yml` 的注释中；需要调整时再取消注释并替换对应的固定默认值。

默认认证为 Basic：用户名 `admin`、租户 `development`，密码由必填项 `OPENAGENT_AUTH_BASIC_PASSWORD` 提供，Engine 与 Router 共享。配置密码后内置开发账号（`admin/admin`、`test/test`）即失效；匿名访问默认关闭。生产 OIDC 元数据固定要求 HTTPS，JWT 时钟容差固定 60 秒。

选择 Keycloak 时额外启动其 profile，并按应用 Compose 注释将 Engine 与 Router 的 `Authentication__Mode` 改为 `JwtBearer`、启用 `Authentication__EnableKeycloak`。同时按基础设施 Compose 注释填写 Keycloak 管理员凭据和公开地址。Basic 无需这些配置。

## 镜像构建与导出

构建脚本支持本机 Docker 和 WSL Docker。未指定模式时会自动检测当前可用的 Docker；也可以使用
`--docker-mode auto`、`--docker-mode docker` 或 `--docker-mode wsl-docker` 强制选择。部署也使用同一模式，
确保镜像位于同一个 Docker daemon：

```bash
# 本机 Docker
scripts/build-images.sh --env-file .env --docker-mode docker
scripts/deploy.sh --env-file .env --docker-mode docker

# WSL Docker（在 WSL 终端中执行）
scripts/build-images.sh --env-file .env --docker-mode wsl-docker
scripts/deploy.sh --env-file .env --docker-mode wsl-docker
```

先构建镜像，再部署；构建脚本不会启动容器，部署脚本不会执行构建。Chat 镜像不包含部署地址，
构建时无需传入域名、端口或租户，同一个镜像可用于不同环境。也可以在 `.env` 中设置
`OPENAGENT_DOCKER_MODE=auto`、`docker` 或 `wsl-docker`，省略命令行参数；命令行 `--docker-mode` 优先于 `.env` 中的设置。

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

### OpenCode 版本锁定与镜像发布

Engine 镜像在构建期内嵌 OpenCode 的 Linux 原生二进制，运行容器不需要 Node/npm。内嵌版本由
`Backend/opencode.version` 统一锁定：`scripts/build-images.sh`/`build-images.ps1` 读取该文件并经
`OPENCODE_VERSION` 构建参数传入，镜像 label 记录 `io.openagent.opencode-version`。升级 OpenCode 的流程是
先修改版本文件，用 `OPENAGENT_OPENCODE_VERSION` 临时覆盖跑通验证（或直接改文件），再以
`docker build --target runtime-tests -f Backend/src/OpenAgent.Engine.Host/Dockerfile .` 通过真实进程回归后提交。

推送 `v*.*.*` Git 标签会触发 [Release 工作流](../../.github/workflows/release.yml)：先执行上述
runtime-tests 真实回归门禁，通过后将 `openagent-engine`、`openagent-router` 与
`openagent-chat` 三个镜像推送到 GHCR，并上传包含 `deploy/` 与版本说明的 Compose 部署包制品。Engine
镜像的行为依赖内嵌 OpenCode 版本，版本标签带 `-oc<OpenCode 版本>` 后缀（如 `1.2.3-oc1.19.3`）以区分
不同 OpenCode 基线；预发布标签（如 `v1.0.0-rc1`）不移动 `latest`。registry 版 Chat 镜像
不包含后台地址，可以直接复用于不同部署环境。

## 前端连接

Chat 是独立静态前端，可以与后台部署在不同服务器。首次打开页面时，选择 Router 或直连 Engine，
填写当前模式的一个完整 HTTP(S) 后台地址，连接成功后进入登录；地址保存在当前浏览器，后续自动使用。
生产、预览及本地开发均不猜测 localhost 或当前页面主机的后台端口，未配置时直接显示连接页。
切换后台地址会清除旧服务的登录状态，再读取目标服务的认证配置。Router 与 Engine 默认允许任意来源的跨域请求，
支持任意方法和请求头；认证与租户授权照常执行。通配模式不启用跨域 Cookie，使用 Authorization 头发送凭据。
如需显式白名单，见[环境变量指南](environment-variables.md)。OIDC 的前端回调和 Web Origin 仍需在身份提供方单独配置。

连接只保存在浏览器；未保存连接时显示连接页。修改页面连接即可切换后台，无需重新构建镜像。
本地 `pnpm dev`、`pnpm preview` 和 Chat 镜像使用相同流程，不读取前端环境变量或部署配置文件。
已有部署首次升级仍需构建新版 Chat 镜像。配置边界见[环境变量指南第 7 节](environment-variables.md#7-前端连接)。

## 应用更新与排障

浏览器访问 `https://openagent.intra.example:8081`；Router 与 Engine 分别使用 `https://openagent.intra.example:8082`
和 `https://openagent.intra.example:8083`。Nginx 的默认
请求体限制为 1100 MB（需要调整时修改应用 Compose 中的 Nginx 环境项），允许通过 Router 上传大文件
（前端单文件上限 100 MB、单次会话附件总量 1000 MB）；后端仍会按 FileAssets 与 Skill 包自身的大小限制校验请求。

Compose 不包含任何模型凭据或已发布 Agent。默认 Basic 登录密码由必填项提供，没有内置账号；
需要企业身份源时按 Compose 注释切换到 HTTPS 的企业 IdP 与 OIDC。

镜像更新时依次执行构建与部署；仅修改运行时配置（例如 OTLP 地址）时只执行部署。三个应用镜像标签可分别由
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
用户与租户组织需要在 Keycloak 管理台中手动创建，详细命令参见 [Keycloak 本地认证集成](../integrations/keycloak.md)。

默认使用 Compose 内置的 MinIO（bucket `openagent-files`）。接入外部 S3/MinIO 时，可覆盖
`OPENAGENT_S3_SERVICE_URL`、`OPENAGENT_S3_BUCKET`、`OPENAGENT_S3_ACCESS_KEY`、
`OPENAGENT_S3_SECRET_KEY`；仅在自签名证书场景显式设置
`OPENAGENT_ALLOW_INSECURE_TLS=true`。

如果服务器容器没有正确安装内部 CA，可在确认所有出站地址可信的前提下设置
`OPENAGENT_ALLOW_INSECURE_TLS=true`，让 LLM、S3、MCP、RAG 及其他平台 HTTP 请求跳过 TLS 证书校验。
该选项默认关闭，生产环境优先安装并信任内部 CA；它同时覆盖平台 HTTP 客户端和 JWT/OIDC 认证回调。

## 日志中心

将外部 Collector 地址写入 `.env` 的 `OPENAGENT_OTLP_ENDPOINT` 后执行部署脚本。Exporter、信号出口和 Trace 关联规则统一见 [可观测性](../overview/Observability.md)；本仓库不管理 Collector 容器。Nginx JSON 访问日志保留 `traceparent` 与 `X-Trace-Id`，供部署侧日志中心关联。

## 已有部署与访问地址

迁移已有部署时，必须把 `OPENAGENT_INFRA_PROJECT`、`OPENAGENT_COMPOSE_PROJECT` 和
`OPENAGENT_INFRA_NETWORK` 设为现有项目/网络名称，或继续使用原来的 `-p`。
目录移动不迁移数据；项目名变化会选择不同的数据卷，包括 Data Protection 密钥卷。
不执行 `down -v`，也不重建已有 Realm 来应用地址变更。

默认公网主机是 localhost：Chat 8081、Router 8082、Engine 8083、Keycloak 58081，
分别通过 HTTPS 访问。容器之间使用服务别名和内部端口；宿主端口修改不影响内部连接。
Keycloak 直接提供 HTTPS，内部 HTTP 8080 不发布到宿主机。
应用栈创建独立的 `openagent` 网络；Engine 和 Router 额外加入基础设施网络以访问数据库、Redis、MinIO 和 Keycloak，
Chat、Nginx 不直接加入基础设施网络。

选用 Keycloak 时，`OPENAGENT_CHAT_PUBLIC_URL` 用于回调、Web Origin 和退出回调，默认 `https://localhost:8081`；
更换域名或端口时按基础设施 Compose 注释同步覆盖，Keycloak 的公开 URL 必须匹配其 HTTPS 对外入口，
Engine/Router 使用同一值作为 Authority。已有 Realm 的 Client 地址需通过管理台更新，
启动导入文件不是已有配置的更新机制。

默认证书为 `deploy/openagent/nginx/certs/tls.crt` 和 `tls.key`，两套服务共用。
自定义时按应用 Compose 注释替换 Nginx 的证书目录挂载。证书和私钥不应进入 Git 或镜像。
`.env` 只包含必填凭据，没有可复用的默认账号；数据库、MinIO 与 Basic 登录密码均由部署者提供。

对象存储使用 `OPENAGENT_S3_SERVICE_URL` 供引擎内部访问。客户端对外下载与分享使用平台端点（`/api/v1/share/{token}`、认证下载），S3 无需公网域名；底层存储实现仍保留签名能力。默认保持 TLS 校验。

## 认证边界

部署默认 Basic：校验配置的账号密码（`OPENAGENT_AUTH_BASIC_PASSWORD` 必填，固定时间比较），
用户名 `admin`、租户 `development`；配置密码后内置开发账号仅在 Development 且未配置密码时可用。
可选企业 IdP 走 OIDC/JWT Bearer，验证 issuer、audience、签名和有效期。
认证只负责建立身份，角色、Agent ACL、能力权限与租户授权由独立的服务端授权层判断。
详见 [安全设计文档](../modules/security.md)。


## OpenCode 服务身份与已有密钥卷

Engine 镜像以非 root `app`（UID 1654）启动，系统 home 为 `/home/app`，OpenCode 私有状态位于 `/var/lib/openagent/opencode`。不要挂载个人 HOME、开发机技能目录或 Docker socket。该部署仅支持可信开发/单一信任域；多租户隔离边界及 N03/N04 可用模式见 [OpenCode 运行时](../integrations/opencode-runtime.md#n02宿主继承缓解与受支持模式)。

首次创建的 Data Protection 卷从新镜像取得服务账户目录权限。已有部署必须在切换镜像前停止 Engine、备份原有 `engine-data-protection` 实际命名卷（保留原 Compose 项目名），再将同一卷的所有权调整为镜像的 APP_UID。Compose 仅把原卷挂载位置从 `/root/.aspnet/DataProtection-Keys` 改到 `/home/app/.aspnet/DataProtection-Keys`，不移动/生成/删除密钥。未迁移权限时新账户可能无法读取旧密钥；不能通过重新录入全部模型密钥来掩盖迁移错误。

以下为运维操作模板，替换准确卷名和新镜像后再执行；不要操作其他项目的卷，也不要输出 key XML：

```sh
# Engine 停止且卷已备份后执行；只挂载原密钥卷，不启动服务。
docker run --rm --user 0 --entrypoint sh \
  -v '<原有实际密钥卷名>:/keys' '<新 Engine 镜像>' \
  -c 'chown -R "$APP_UID:$APP_UID" /keys && chmod 700 /keys && find /keys -type f -exec chmod 600 {} +'
```

用原有 Compose 项目名启动新镜像后，验证 readiness、既有模型凭据解密和一轮聊天，再结束维护窗口。回退时停止 Engine，将镜像与 Compose 挂载目标一起回退，保留同一个密钥卷；旧 root 镜像可读取迁移后的所有者权限。不要删除卷或重新生成密钥。若运维自定义了 UID/只读文件系统/挂载路径，须按实际身份预置 home、状态目录与卷 ACL，并单独验证。
