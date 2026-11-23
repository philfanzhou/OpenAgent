# 部署指南

本指南基于 `dev`：Engine（.NET 10 + OpenCode ACP）、Router、Chat；没有独立 Runner。
基础设施配置与 Realm 保持不变。以下命令在 Linux、macOS 或 WSL 的仓库根目录执行，要求 Docker Compose >= 2.20。

## 配置与启动

```bash
scripts/deploy.sh init
# 填写 deploy/openagent/ 的共享/专用 .env，以及 deploy/nginx/.env。
scripts/build-images.sh
scripts/deploy.sh check
scripts/deploy.sh
```

`init` 只将缺失的 `.env.example` 复制为 `.env`（0600），不覆盖已有文件。
数据库连接串、S3 API 地址和凭据直接填入 Engine 文件；入口主机和端口直接填入 Nginx 文件。
可选项全部注释并说明用途。默认保留当前 dev Compose 的 Production + JwtBearer / Keycloak 选择；
共享文件中的公开 Issuer、内部 HTTPS discovery、Audience 和 ClientId 必须匹配现有 IdP。
应用 appsettings 自身默认 Basic，与部署选择不同；如改用 Basic，须同时设置共享文件中的模式和密码。

先准备已有基础设施和有效证书，再运行部署。脚本只使用本地镜像，不自动 build / pull。
默认先部署 Nginx 并由其创建网络，再启动业务并等待健康检查，也可分开执行：

```bash
scripts/deploy.sh nginx  # 不要求数据库/S3凭据或业务镜像，可先启动入口
scripts/deploy.sh app    # 业务就绪后加入 Nginx 网络
```

`check` 校验环境文件格式/路径、证书存在、Compose 结构及网络归属，不启动服务。
它不验证数据库、S3 或 IdP 的真实连接与凭据；这些由服务启动和健康检查验证。
Nginx 失败时不启动业务；业务失败时保留入口。脚本不会操作 infrastructure。
维护页与维护开关尚未实现，本次提供独立入口生命周期。

## 固定名称与环境文件

项目固定为 `openagent-nginx` / `openagent-app`；Nginx 管理 `openagent` 网络，应用以 external 方式加入。
Engine/Router 另加入既有 `openagent-infrastructure` 网络。项目、网络和镜像名称不接受环境变量覆盖。
镜像固定为 `openagent-engine:latest`、`openagent-router:latest`、`openagent-chat:latest` 和 `nginx:latest`。

| 示例文件（以 deploy/ 为根） | 对应 .env 的用途 |
|---|---|
| [openagent/.env.example](../../deploy/openagent/.env.example) | Engine / Router 共用环境、Redis、认证、遥测 |
| [openagent/engine/.env.example](../../deploy/openagent/engine/.env.example) | 数据库、对象存储、心跳、Node/OpenCode TLS |
| [openagent/router/.env.example](../../deploy/openagent/router/.env.example) | Engine 地址、意图识别超时和 Intern-Decision 密钥 |
| [nginx/.env.example](../../deploy/nginx/.env.example) | 入口主机、端口、模板变量、证书目录与维护模式目录 |

文件位置固定；Engine/Router 先读共享文件，再读专用文件，同名项以后者为准。
应用的三个文件直接填写 .NET 原生 `__` 配置，由 Compose `env_file` 加载；不再有顶层 `deploy/.env` 或 S3/数据库别名转译。
应用 Compose 显式使用 `deploy/openagent/.env` 作为 CLI 环境文件，避免隐式读取工作目录中的其他 `.env`。
Nginx 的 `.env` 同时供其 Compose `--env-file`（端口、挂载）与容器 `env_file`（模板）使用，部署脚本仅读它做证书预检。
端口变量只控制宿主机发布端口；Nginx 容器内固定监听 Chat `8081`、Router `8082`、Engine `8083`，修改外部端口无需改模板。
原生服务值可包含连接串分号；含 `$`、`#` 的值用单引号包裹，不要用 shell source 应用文件。
修改运行配置后重新部署即可生效。共享认证与遥测字段仅注入 Engine/Router，数据库与 S3 密钥只注入 Engine。

### 监听地址与容器地址

`ASPNETCORE_URLS` 的注释值来自镜像 Dockerfile 的 ENV：Engine 为 `http://+:5208`、Router 为 `http://+:5001`。
这是镜像默认监听地址，省略时仍然生效，因此作为非必填项注释；不是 ASP.NET 通用默认端口。
Router 的 `http://engine:5208` 及 Nginx 的 `engine:5208` / `router:5001` 是 Docker 服务名与内部端口，必须与监听端口一致。
它们不属于 .NET 默认路由：Router 代码兜底是 localhost，不能用于跨容器访问，故其 EngineEndpoint 保持显式配置。

Engine 文件直接配置 `FileAssets__ObjectStorage__ServiceUrl`，示例使用内置 MinIO 地址 `http://minio:9000`；外接 S3 时直接改值。
连接地址与凭据直接填写在所属环境文件中，不嵌入其他变量、默认值分支或错误提示表达式。
Nginx Compose 仅直接引用入口文件中的字段；默认值集中在其 .env.example。服务启动时仍执行自身配置/连接检查。


## 镜像构建与 OpenCode

```bash
scripts/build-images.sh               # 构建 Engine、Router、Chat
scripts/build-images.sh ./dist/images # 额外导出一个 openagent-images.tar
```

Engine 的 OpenCode 版本从 `Backend/opencode.version` 读取并经 `OPENCODE_VERSION` 构建参数传入，
保留平台版本 label。升级版本先修改锁文件，并运行 Engine Dockerfile 的 `runtime-tests` 真实进程门禁。
Chat 源码位于 `Frontend/OpenAgent.Chat/`，镜像不携带部署地址：在登录页连接设置中填写实际 Router/Engine 地址。
Windows 在 WSL 中执行同一套 Bash 脚本，不再维护重复 PowerShell 实现或跨宿主 Docker 模式分支。
发布仓库中的镜像须先拉取/导入并 retag 为上述固定本地名称；入口镜像也需提前准备。

### 启用 OpenCode 文件交换

在 Engine 的 `.env` 同时开启 `FileAssets__Enabled=true` 和 `FileExchange__Enabled=true`，并配置可读写的 S3 存储桶。根据部署环境和任务需要配置 `OpenCode__AllowedNativeTools__0` 等原生工具白名单。处理表格的脚本依赖由执行环境预装。轮次行为与原生工具授权边界见 [OpenCode 运行时](../integrations/opencode-runtime.md)。

## 证书、数据卷与迁移

Nginx 默认证书放在 `deploy/nginx/certs/tls.crt` 和 `tls.key`。
`OPENAGENT_TLS_CERT_DIR=./certs` 相对 `deploy/nginx` 解析，也可直接填写绝对路径。
Keycloak（infrastructure）同样从 `deploy/nginx/certs` 读取证书，与 Nginx 共用同一目录。
已有部署若证书仍在旧目录 `deploy/openagent/nginx/certs`，将其迁移到 `deploy/nginx/certs`（或同时把 Nginx 证书目录指向旧目录）。
终端、应用及 IdP 必须信任所用 CA。

前端维护模式以文件为开关：`deploy/nginx/maintenance/maintenance.html` 存在时，Nginx 对前端（8081）
返回 503 与该页面内容，删除文件即恢复，无需 reload；Router 与 Engine 不受影响。
页面模板见 [maintenance.html.example](../../deploy/nginx/maintenance/maintenance.html.example)：
单文件自包含（内联样式、无脚本与外部资源），修改后复制为 `maintenance.html` 即生效。
目录可用 `OPENAGENT_MAINTENANCE_DIR` 调整，默认 `./maintenance`，相对路径以 `deploy/nginx` 为基准。
仓库不跟踪 `maintenance.html` 本身，避免新部署直接进入维护模式。
Engine 的 `engine-data-protection` 卷继续挂载到 `/home/app/.aspnet/DataProtection-Keys`，保持 dev 非 root 镜像路径。
保留该卷及原密钥，不会自动迁移数据；代码执行由 OpenCode 原生工具负责，不配置 Runner 服务或密钥。

旧自定义项目/网络需先迁移到固定名称。若已有 `openagent` 网络不属于 Nginx，需在维护窗口停止其全部连接容器，
确认无连接后手工移除旧网络，再由 `scripts/deploy.sh nginx` 创建；脚本不自动接管或删除网络。
旧应用项目中的 Nginx 容器也需先停止/移除，释放入口端口。停止整套服务时先停业务，再停入口。

顶层环境文件已移除。已有顶层 `.env` 不会自动转换或删除；请将数据库/S3 值移入 Engine，入口参数移入 Nginx，认证与遥测移入共享文件。
原 `OPENAGENT_OTLP_TRACES_ENDPOINT` / `HEADERS` 对应共享文件中的 OTEL 通用出口；也可显式采用 traces 专用变量，
使审计掩码网关仅接收 traces。旧 Basic 密码迁到 `Authentication__BasicPassword`，Intern-Decision 密钥迁到 Router 文件。
原 dev 的 `NODE_TLS_REJECT_UNAUTHORIZED=0` 改为可选项；依赖自签名回源时需按 Engine 示例显式取消注释，或配置附加 CA。
生产 OIDC metadata 保持 HTTPS；公开 Issuer 与内部 discovery 地址按实际部署分别配置。

## 基础设施与排障

`deploy/infrastructure/` 未改动，仍使用其既有环境文件单独管理 PostgreSQL、Redis、MinIO 和 Keycloak profiles；不要把应用原生文件当成基础设施变量文件。
MinIO 镜像源按原基础设施 Compose 中的说明配置；应用启动会执行待应用的 EF 迁移。
完整变量与默认值见 [环境变量指南](environment-variables.md)，认证见 [Keycloak](../integrations/keycloak.md)，
遥测见 [可观测性](../overview/Observability.md)，OpenCode 的原生工具与版本验证见 [运行时](../integrations/opencode-runtime.md)。
