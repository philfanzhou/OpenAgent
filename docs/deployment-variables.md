# 独立应用环境变量

部署步骤、认证和迁移见 [部署指南](deployment.md)。本文列出正式独立 Compose 的完整变量；`__` 是 .NET 配置层级分隔符。

必填项默认值标为「空（必填）」；不会提供真实可用的默认密钥。可选空值表示不启用或自动派生。

## 必填项

| 应用 | 变量 | 要求 |
|---|---|---|
| Engine | `ConnectionStrings__OpenAgentDatabase` | 完整 PostgreSQL 连接串 |
| Engine | `FileAssets__ObjectStorage__ServiceUrl` / `AccessKey` / `SecretKey` | S3 API Endpoint 与凭据；后三者均使用相同的 `FileAssets__ObjectStorage__` 前缀 |
| Engine、Router | `Authentication__BasicPassword` | Basic 模式必填；两端用户名、密码和租户保持一致 |
| Runner | `Runner__ApiKey` | 至少 32 字符；启用代码执行时复制到 Engine 的 `CodeExecution__ApiKey` |
| Engine、Router | `Authentication__Authority` | 仅切换 JwtBearer 时必填；Basic 不需要 Keycloak |

Bucket 默认 `openagent-files`，该 bucket 必须已存在；Region 和寻址方式按服务商要求设置。Router、Runner、Chat 不接收数据库或 S3 凭据。

## Engine

模板：[ engine.env.example ](../deploy/openagent/engine.env.example)。

| 变量 | 默认值 | 用途 |
|---|---|---|
| `OPENAGENT_COMPOSE_PROJECT` | `openagent-engine` | 独立部署项目名；已有部署升级时保留原值 |
| `OPENAGENT_ENGINE_IMAGE` | `openagent-engine:latest` | 应用镜像 |
| `OPENAGENT_ENGINE_PORT` | `5208` | 宿主机端口 |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` | 共享 Docker 网络；独立服务不声明启动依赖 |
| `ConnectionStrings__OpenAgentDatabase` | 空（必填） | 必填：完整 PostgreSQL 连接串，无默认连接或密码 |
| `FileAssets__ObjectStorage__ServiceUrl` | 空（必填） | 必填：S3 API Endpoint，不能使用控制台地址 |
| `FileAssets__ObjectStorage__AccessKey` | 空（必填） | 必填：S3 Access Key |
| `FileAssets__ObjectStorage__SecretKey` | 空（必填） | 必填：S3 Secret Key |
| `ASPNETCORE_ENVIRONMENT` | `Production` | ASP.NET Core 环境 |
| `ConnectionStrings__Redis` | 空 | 可选 Redis；留空时不使用 Redis |
| `FileAssets__ObjectStorage__BucketName` | `openagent-files` | S3 bucket；需事先创建 |
| `FileAssets__ObjectStorage__Region` | `us-east-1` | 签名区域，按存储商要求覆盖 |
| `FileAssets__ObjectStorage__ForcePathStyle` | `false` | 是否采用 path-style；MinIO 通常设为 true |
| `FileAssets__ObjectStorage__KeyPrefix` | `files` | 对象 key 前缀 |
| `FileAssets__Share__PublicBaseUrl` | 空 | 可选文件分享公开地址 |
| `Heartbeat__AdvertisedHost` | `engine` | Router 能访问的 Engine 主机名 |
| `Heartbeat__AdvertisedPort` | `5208` | Engine 内部端口 |
| `CodeExecution__Enabled` | `false` | 是否启用独立 Runner |
| `CodeExecution__Endpoint` | `http://runner:5209` | 启用代码执行时的 Runner 地址 |
| `CodeExecution__ApiKey` | 空 | 启用代码执行时必填，与 Runner__ApiKey 相同，至少 32 字符 |
| `CodeExecution__RequestTimeoutSeconds` | `180` | Runner HTTP 超时，秒 |
| `Authentication__Mode` | `Basic` | 认证方式；Basic 或 JwtBearer |
| `Authentication__BasicUsername` | `admin` | Basic 用户名 |
| `Authentication__BasicPassword` | 空 | Basic 密码；Basic 模式必填，无内置部署密码 |
| `Authentication__BasicTenantId` | `default` | Basic 用户所属租户；Engine 与 Router 必须一致 |
| `Authentication__EnableKeycloak` | `false` | 启用 Keycloak 登录入口；需同时选择 JwtBearer |
| `Authentication__EnableApiKey` | `false` | 附加第三方 API Key 认证 |
| `Authentication__AllowDevelopmentAnonymous` | `false` | 仅 Development 可启用匿名访问 |
| `Authentication__Authority` | 空 | JwtBearer 必填：OIDC issuer |
| `Authentication__Audience` | `openagent-api` | JWT audience |
| `Authentication__ClientId` | `openagent-chat` | OIDC client ID |
| `Authentication__MetadataAddress` | 空 | 可选 discovery 地址；为空时由 Authority 派生；生产须 HTTPS |
| `Authentication__RequireHttpsMetadata` | `true` | 校验 HTTPS 元数据；生产必须 true |
| `Authentication__ClockSkewSeconds` | `60` | JWT 时钟容差，秒 |
| `Cors__AllowedOrigins__0` | `http://localhost:8081` | 浏览器前端来源 |
| `OpenTelemetry__OtlpEndpoint` | 空 | 可选外部 OTLP Collector 地址 |
| `OPENAGENT_ALLOW_INSECURE_TLS` | `false` | 是否跳过出站 TLS 证书校验 |

## Router

模板：[ router.env.example ](../deploy/openagent/router.env.example)。

| 变量 | 默认值 | 用途 |
|---|---|---|
| `OPENAGENT_COMPOSE_PROJECT` | `openagent-router` | 独立部署项目名；已有部署升级时保留原值 |
| `OPENAGENT_ROUTER_IMAGE` | `openagent-router:latest` | 应用镜像 |
| `OPENAGENT_ROUTER_PORT` | `5001` | 宿主机端口 |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` | 共享 Docker 网络；独立服务不声明启动依赖 |
| `ASPNETCORE_ENVIRONMENT` | `Production` | ASP.NET Core 环境 |
| `RouterSettings__Routing__EngineEndpoint` | `http://engine:5208` | 必需的 Engine 依赖地址；同 Docker 网络可用默认值 |
| `RouterSettings__IntentRecognition__TimeoutMs` | `30000` | 意图识别超时，毫秒 |
| `ConnectionStrings__Redis` | 空 | 可选 Redis；留空使用静态路由与本地降级 |
| `Authentication__Mode` | `Basic` | 认证方式；Basic 或 JwtBearer |
| `Authentication__BasicUsername` | `admin` | Basic 用户名 |
| `Authentication__BasicPassword` | 空 | Basic 密码；Basic 模式必填，无内置部署密码 |
| `Authentication__BasicTenantId` | `default` | Basic 用户所属租户；Engine 与 Router 必须一致 |
| `Authentication__EnableKeycloak` | `false` | 启用 Keycloak 登录入口；需同时选择 JwtBearer |
| `Authentication__EnableApiKey` | `false` | 附加第三方 API Key 认证 |
| `Authentication__AllowDevelopmentAnonymous` | `false` | 仅 Development 可启用匿名访问 |
| `Authentication__Authority` | 空 | JwtBearer 必填：OIDC issuer |
| `Authentication__Audience` | `openagent-api` | JWT audience |
| `Authentication__ClientId` | `openagent-chat` | OIDC client ID |
| `Authentication__MetadataAddress` | 空 | 可选 discovery 地址；为空时由 Authority 派生；生产须 HTTPS |
| `Authentication__RequireHttpsMetadata` | `true` | 校验 HTTPS 元数据；生产必须 true |
| `Authentication__ClockSkewSeconds` | `60` | JWT 时钟容差，秒 |
| `Cors__AllowedOrigins__0` | `http://localhost:8081` | 浏览器前端来源 |
| `OpenTelemetry__OtlpEndpoint` | 空 | 可选外部 OTLP Collector 地址 |
| `OPENAGENT_ALLOW_INSECURE_TLS` | `false` | 是否跳过出站 TLS 证书校验 |

## Runner

模板：[ runner.env.example ](../deploy/openagent/runner.env.example)。

| 变量 | 默认值 | 用途 |
|---|---|---|
| `OPENAGENT_COMPOSE_PROJECT` | `openagent-runner` | 独立部署项目名；已有部署升级时保留原值 |
| `OPENAGENT_RUNNER_IMAGE` | `openagent-runner:latest` | 应用镜像 |
| `OPENAGENT_RUNNER_PORT` | `5209` | 宿主机端口 |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` | 共享 Docker 网络；独立服务不声明启动依赖 |
| `Runner__ApiKey` | 空（必填） | 必填：至少 32 字符；与 Engine 的 CodeExecution__ApiKey 一致 |
| `ASPNETCORE_ENVIRONMENT` | `Production` | ASP.NET Core 环境 |
| `Runner__TimeoutSeconds` | `120` | 单次执行超时，秒 |
| `Runner__MaxConcurrentExecutions` | `2` | 并发执行数 |
| `Runner__MemoryMiB` | `1536` | 单沙箱内存限制 MiB |
| `Runner__WorkspaceMiB` | `128` | 工作区限制 MiB |
| `Runner__MaxProcesses` | `64` | 单沙箱进程数上限 |
| `Runner__SessionIdleMinutes` | `120` | 会话空闲过期时间，分钟 |
| `Runner__MaxSessionSandboxes` | `64` | 会话沙箱上限 |
| `Runner__WorkspaceRoot` | `/var/lib/openagent-runner/workspaces` | 容器工作区根目录 |
| `Runner__BubblewrapPath` | `/usr/bin/bwrap` | 容器 bwrap 路径 |
| `Runner__PythonPath` | `/opt/openagent-code/venv/bin/python` | 容器 Python 路径 |
| `Runner__NodePath` | `/usr/bin/node` | 容器 Node 路径 |

## Chat

模板：[ chat.env.example ](../deploy/openagent/chat.env.example)。

| 变量 | 默认值 | 用途 |
|---|---|---|
| `OPENAGENT_COMPOSE_PROJECT` | `openagent-chat` | 独立部署项目名；已有部署升级时保留原值 |
| `OPENAGENT_CHAT_IMAGE` | `openagent-chat:latest` | 应用镜像 |
| `OPENAGENT_CHAT_PORT` | `8081` | 宿主机端口 |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` | 共享 Docker 网络；独立服务不声明启动依赖 |

基础设施和可选 HTTPS gateway 的变量见 [基础设施变量](deployment-infrastructure-variables.md)。

## S3 兼容服务

Engine 使用 AWS S3 SDK，可覆盖 Endpoint、Region、Bucket、Access Key、Secret Key 和 ForcePathStyle。已配置 `WHEN_REQUIRED` 校验和策略以兼容第三方实现。

| 服务类型 | Endpoint | Region | ForcePathStyle |
|---|---|---|---|
| AWS S3 | 对应区域的 S3 API Endpoint | bucket 所在区域 | `false` |
| MinIO | 例如 `http://minio:9000`，不是 9001 控制台 | 实例配置区域，常用 `us-east-1` | 通常 `true` |
| 其他 S3 兼容存储 | 服务商提供的 S3 API Endpoint | 服务商要求的签名区域 | 按服务商文档设置 |

兼容范围取决于服务商对 S3 API 和 SigV4 的实现。部署前验证 PutObject、GetObject、GetBucketLocation 和预签名 GET；当前 readiness 会调用 GetBucketLocation，账号也需相应权限。预签名 URL 中的存储地址必须能被使用该 URL 的客户端访问。未对每一家服务商执行真实联测。

参考：[AWS SDK 配置](https://docs.aws.amazon.com/sdkfornet/v3/apidocs/items/S3/TS3Config.html)、[AWS 校验和说明](https://docs.aws.amazon.com/sdkref/latest/guide/feature-dataintegrity.html)。
