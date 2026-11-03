# 部署指南

每个应用独立部署、独立配置。完整变量、默认值、必填项和 S3 接入说明见 [环境变量清单](deployment-variables.md)，模板入口见 [deploy](../deploy/README.md)。

## 必填与默认行为

- Engine 必填 PostgreSQL 连接串、S3 API Endpoint、Access Key、Secret Key。Bucket 默认 `openagent-files`，必须已创建；数据库和 S3 可以使用外部服务。
- Engine、Router 默认 `Basic`，用户名默认 `admin`，密码无默认值；两端用户名、密码、租户保持一致。默认租户为 `default`。
- Keycloak 默认关闭，Basic 不需要任何 OIDC 参数。Redis 可选；Runner 仅在 Engine 开启代码执行时需要，届时配置相同的至少 32 字符的 API Key。
- 应用间通过配置地址连接，Compose 不声明彼此的启动依赖。跨机器部署时覆盖 Engine/Runner 地址；单机可使用共享 Docker 网络中的服务名。

## 独立启动

示例配置保存在仓库外，分别复制并填写顶部的必填项。`.env` 由 Compose 读取，不是 shell 脚本；包含空格、分号、`$` 或 `#` 的值建议用单引号包裹。

```bash
mkdir -p "$HOME/openagent-config"
cp deploy/openagent/engine.env.example "$HOME/openagent-config/engine.env"
cp deploy/openagent/router.env.example "$HOME/openagent-config/router.env"
chmod 600 "$HOME/openagent-config/"*.env
# 编辑两个文件，填写 Engine 的数据库/S3 和两端一致的 Basic 密码。
# 准备镜像：构建下方源码镜像，或使用 docker pull 拉取相应标签。
scripts/deploy.sh --app engine --env-file "$HOME/openagent-config/engine.env"
scripts/deploy.sh --app router --env-file "$HOME/openagent-config/router.env"
```

部署脚本只处理选定应用，验证 Compose、检查本地镜像、创建所需共享网络并等待容器启动及已有的健康检查通过，不构建、不自动拉取镜像。默认网络为 `openagent-infrastructure`；也支持 `--docker-mode wsl-docker`（Windows 推荐在 WSL 内执行）。脚本依赖 Docker Compose 和 Python 3。

也可直接执行 `docker compose --env-file /absolute/path/router.env -f deploy/openagent/router.compose.yml up -d --wait`；直接执行前需创建模板指定的共享网络。默认直连端口：Engine 5208、Router 5001、Runner 5209、Chat 8081。Basic 部署对外访问时由现有 HTTPS 反向代理或可选 gateway 提供 TLS。

## 可选应用和基础设施

Runner、Chat、gateway、PostgreSQL、Redis、MinIO、Keycloak 各有自己的 `.env.example` 和 `.compose.yml`。复制需要的模板后执行 `scripts/deploy.sh --app <名称> --env-file <文件>`，应用名称为 `runner/chat/gateway/postgres/redis/minio/keycloak`。

- 使用外部数据库或 S3 时无需部署 PostgreSQL 或 MinIO；本地 MinIO API 地址为 `http://minio:9000`，`ForcePathStyle=true`。将 MinIO 凭据和 bucket 对应填入 Engine；不会自动跨配置文件同步密钥。
- 部署 Runner 后，将 Engine 的 `CodeExecution__Enabled` 改为 `true`，配置 `CodeExecution__ApiKey`。Runner 使用 Bubblewrap，需专用可信 Linux 主机/VM 支持。
- Chat 在独立前端仓库发布；此仓库只启动前端镜像，不构建前端。浏览器连接配置由前端仓库维护，不向前端注入数据库、S3 或 Basic 密码。
- gateway 使用原有三个 HTTPS 入口，需准备 `tls.crt/tls.key`。启用时将 Chat 直连端口改为 18081，避免与 gateway 8081 冲突；修改两端 CORS 为实际 HTTPS 前端来源。
- Keycloak 仅在需要时单独部署，配置证书和对外地址。两端切换 `Authentication__Mode=JwtBearer`，填写 Authority，启用 `EnableKeycloak=true`；非 Keycloak OIDC 也可使用 JwtBearer。生产 discovery 必须走可信 HTTPS。

`deploy/openagent/docker-compose.yml` 是可选组合入口，按 `deployment.env.example` 指定的四份应用文件加载配置，要求 Compose >= 2.20。它会显式启动四个应用（包括 Runner），不包含 gateway 或 Keycloak。基础设施的组合入口同理读取 postgres/redis/minio 三份文件；独立部署无需准备其他应用的文件。

## 构建与升级

```bash
scripts/build-images.sh --app engine --docker-mode docker
scripts/build-images.sh --app all --tar-dir ./dist/images
# 可选 --env-file deploy/build.env.example 仅控制镜像标签，不需要运行时凭据。
# PowerShell：pwsh -File scripts/build-images.ps1 -Application engine
```

源码构建使用根目录 `src/`，只处理 Engine、Router、Runner。部署配置和 TLS 私钥由 `.dockerignore` 排除。运行时参数修改后仅重新部署对应应用；变量覆盖顺序遵循 Compose，当前 shell 中同名变量会覆盖 `--env-file`。

生产首次启动前需要执行数据库迁移；现有 `Database:ApplyMigrationsOnStartup` 仅在 Development 生效。可使用 .NET EF 工具对 `src/OpenAgent.Infrastructure` 执行迁移，设计时工厂读取 `ConnectionStrings__OpenAgentDatabase`；应通过部署环境注入连接串，不把真实凭据写入命令记录。备份、认证和数据管理仍见对应模块文档；配置管理 API 当前仅在 Development 映射。

## 已有部署迁移

拆分前请记录项目名、网络名、卷名和现有凭据。已有应用的 `engine-data-protection` 卷必须继续使用；它负责解密数据库中的历史密钥。

- 新模板默认项目为 `openagent-engine/router/runner/chat`。已有 Engine 设置 `OPENAGENT_COMPOSE_PROJECT` 为原应用项目名（通常 `openagent-app`），以复用其 Data Protection 卷。
- 基础设施默认项目仍为 `openagent-infrastructure`；已有 PostgreSQL、Redis、MinIO、Keycloak 保留原项目名和对应卷。
- 旧 `OPENAGENT_POSTGRES_* / OPENAGENT_S3_* / OPENAGENT_AUTH_*` 不再直接注入应用。按变量表迁移为 .NET 原生键名；外部端口映射不能代替连接串中的容器内部端口。
- 若已有数据属于 `development` 租户，将两端 `Authentication__BasicTenantId` 设置为 `development`，避免默认租户变化后看不到原数据。
- 切换时停止相应旧容器后启动新实例；不要使用 `down -v` 或 `--remove-orphans` 删除仍在使用的卷或其他独立服务。

并行预览仍由 [预览指南](parallel-previews.md) 管理，使用其专用开发参数，不作为正式部署模板。
