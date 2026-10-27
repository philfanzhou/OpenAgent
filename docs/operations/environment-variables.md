# 环境变量指南

本文件是 OpenAgent 环境变量的唯一事实源：覆盖 Engine.Host、Router、Runner 三个宿主实际读取的全部配置环境变量、Compose 部署层的 `OPENAGENT_*` 包装变量、前端构建变量，以及不同场景的最小配置集。部署流程本身见 [部署指南](deployment.md)。

## 1. 配置加载规则

三个宿主均使用 `WebApplication.CreateBuilder` 默认配置管线（`Backend/src/OpenAgent.Engine.Host/Program.cs`、`Backend/src/OpenAgent.Router/Program.cs`、`Backend/src/OpenAgent.Runner/Program.cs`），**没有应用级环境变量前缀**：

- 优先级从低到高：`appsettings.json` → `appsettings.{Environment}.json` → User Secrets（仅 Development）→ **全部环境变量** → 命令令行。
- 环境变量用双下划线 `__` 表示配置层级，等价于 `:`。例如 `Authentication__Mode` 覆盖 `appsettings.json` 的 `Authentication:Mode`。数组用 `节__键__0`、`节__键__1` 形式。
- 不存在 `OPENAGENT__SECTION__KEY` 之类的应用前缀；`deploy/` 里的 `OPENAGENT_*` 是 **Compose/脚本插值变量**，由 compose 文件转换成 `节__键` 形式再注入容器（见第 6 节）。

少数变量绕过 IConfiguration 直读：`OTEL_EXPORTER_OTLP_ENDPOINT`（OTLP 回退地址）、`ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS`（心跳端口探测）、`PATH` / `PATHEXT` 等（OpenCode 子进程可执行解析与最小环境透传）。

## 2. 场景速查

| 场景 | 需要设置的环境变量 |
|---|---|
| 本地裸机开发（`dotnet run`） | **零配置**。Development 配置默认 Basic 匿名登录、连接本机 PostgreSQL（`localhost:5432`，账号 openagent/openagent）。前置条件：本机可用的 PostgreSQL 与 `opencode` 可执行；否则见第 5.2 / 5.6 节覆盖 |
| 本地 Docker 全栈（Keycloak） | 复制 `deploy/deployment.env.example` 为 `.env`，**必填 `OPENAGENT_RUNNER_API_KEY`**（≥32 字符）；其余走 Compose 兜底值。默认 `OPENAGENT_ASPNETCORE_ENVIRONMENT=Development` |
| 内网/生产部署 | `OPENAGENT_ASPNETCORE_ENVIRONMENT=Production`；`OPENAGENT_PUBLIC_HOST`（+443 时设置 `OPENAGENT_CHAT_PORT=443` 等）；覆盖数据库/MinIO/Keycloak 管理员默认凭据；`OPENAGENT_RUNNER_API_KEY`；`OPENAGENT_KEYCLOAK_COMMAND=start`。认证变量见第 3 节 |
| 不启用代码执行沙箱 | 不部署 Runner，并在 Engine 侧设 `CodeExecution__Enabled=false`（默认即 false；应用 Compose 当前写死 `true` 并依赖 Runner 健康检查） |
| 外接 S3/MinIO | `OPENAGENT_S3_SERVICE_URL`、`OPENAGENT_S3_BUCKET`、`OPENAGENT_S3_ACCESS_KEY`、`OPENAGENT_S3_SECRET_KEY`（等价 `FileAssets__ObjectStorage__*`） |
| 接入日志中心 | `.env` 设置 `OPENAGENT_OTLP_ENDPOINT`（OTLP gRPC/HTTP 地址）；规则见 [可观测性](../overview/Observability.md) |
| 自签证书内网 | `OPENAGENT_ALLOW_INSECURE_TLS=true`（逃生门，默认关闭；详见第 5.13 节） |

## 3. 启用 Keycloak（JwtBearer）

Engine 与 Router 共用同一套认证（都经 `AddAgentHost` 组合，`Backend/src/OpenAgent.Engine/Hosting/ServiceCollectionExtensions.cs`），**两个服务的变量必须设置一致**；应用 Compose 已把同一组 `Authentication__*` 同时注入 engine 与 router。

### 3.1 需要改动的变量

| 环境变量 | Compose 包装 | 必填 | 默认（生效值） | 说明 |
|---|---|---|---|---|
| `Authentication__Mode=JwtBearer` | `OPENAGENT_AUTH_MODE` | 是 | `JwtBearer`（镜像 json 与 Compose 兜底均是） | 切到 JWT 校验 |
| `Authentication__Authority` | 由 `OPENAGENT_KEYCLOAK_PUBLIC_URL`（或 `OPENAGENT_PUBLIC_HOST`+`OPENAGENT_KEYCLOAK_PORT` 派生）+`/realms/openagent` | **是** | 生产 json 置空 → **必须提供** | OIDC 签发方，须绝对 HTTP(S) URI |
| `Authentication__Audience` | `OPENAGENT_AUTH_AUDIENCE` | **是** | 空串（json）/ `openagent-api`（Compose 兜底） | JWT audience 校验 |
| `Authentication__ClientId` | `OPENAGENT_AUTH_CLIENT_ID` | **是** | 空串（json）/ `openagent-chat`（Compose 兜底） | OIDC 客户端 ID，透传给前端登录页 |
| `Authentication__MetadataAddress` | `OPENAGENT_KEYCLOAK_METADATA_ADDRESS` | 否 | `http://keycloak:8080/realms/openagent/.well-known/openid-configuration` | 服务端 discovery 地址（浏览器不可达的容器内地址） |
| `Authentication__RequireHttpsMetadata` | `OPENAGENT_AUTH_REQUIRE_HTTPS_METADATA` | 否 | 代码 true / Compose 兜底 false | 见下方校验规则 |
| `Authentication__ClockSkewSeconds` | `OPENAGENT_AUTH_CLOCK_SKEW_SECONDS` | 否 | `60`（0..300） | JWT 时钟容差，仅用于网络抖动 |
| `Authentication__Scopes__0..n` | — | 否 | json：`openid,profile,email,organization` | 透传给前端 OIDC 请求 |
| `Authentication__EnableKeycloak` | `OPENAGENT_AUTH_ENABLE_KEYCLOAK` | 否 | `true` | **仅控制前端是否显示 OIDC 登录入口**（`/api/v1/auth/config`），不影响服务端 JWT 校验 |
| `Authentication__EnableApiKey` | `OPENAGENT_AUTH_ENABLE_API_KEY` | 否 | `false` | 叠加第三方 API Key 混合模式，见 [认证模块文档](../modules/security/auth/README.md) |

**校验规则**（`Backend/src/OpenAgent.Engine/Hosting/Authentication/AgentAuthenticationOptionsValidator.cs`，启动即校验、失败拒绝启动）：

- `Mode=JwtBearer` 时 `Authority`、`Audience`、`ClientId` 三者缺一即启动失败；
- Authority 必须是绝对 HTTP(S) URI；`RequireHttpsMetadata=true` 时 Authority 必须 HTTPS；
- `RequireHttpsMetadata=false` 仅允许 Development 环境 + localhost Authority（本地 Docker 联调可用；生产置 false 会启动失败）；
- 非 Development 环境 `MetadataAddress` 必须 HTTPS。

### 3.2 Keycloak 侧需要准备

基础设施 Compose（`deploy/infrastructure/docker-compose.yml`）首次启动自动导入 Realm（`deploy/infrastructure/keycloak/realm/openagent-realm.json`）：Realm `openagent`、公开 SPA Client `openagent-chat`（Authorization Code + PKCE）、audience mapper（把 `openagent-api` 写入 access token）、`tenant_id`/`roles` claim mapper。**用户和租户组织需在管理台手动创建**，且用户属性 `tenant_id` 必须与所属 Organization 一致，否则已登录请求会因缺租户 claim 被拒。完整步骤见 [Keycloak 本地认证集成](../integrations/keycloak/README.md)。

### 3.3 最小配置示例

裸机 / 非 Compose 环境（Engine 与 Router 都要设）：

```dotenv
ASPNETCORE_ENVIRONMENT=Production
Authentication__Mode=JwtBearer
Authentication__Authority=https://keycloak.example.com/realms/openagent
Authentication__Audience=openagent-api
Authentication__ClientId=openagent-chat
Authentication__RequireHttpsMetadata=true
```

Compose 部署只需在 `.env` 中（其余走兜底）：

```dotenv
OPENAGENT_PUBLIC_HOST=openagent.intra.example
OPENAGENT_RUNNER_API_KEY=<openssl rand -hex 32>
# 需要覆盖派生值时：
# OPENAGENT_KEYCLOAK_PUBLIC_URL=https://keycloak.intra.example
# OPENAGENT_AUTH_AUDIENCE=openagent-api
# OPENAGENT_AUTH_CLIENT_ID=openagent-chat
```

前端不需要任何认证环境变量：OIDC 参数由 `GET /api/v1/auth/config` 下发。

## 4. 启用 Basic 认证（仅开发）

Basic 是 Development 专用兼容登录，**非 Development 环境设置 `Authentication__Mode=Basic` 会直接启动失败**（`AgentAuthenticationOptionsValidator.cs`）。凭据硬编码为 `admin/admin`、`test/test`（`Backend/src/OpenAgent.Engine/Hosting/Authentication/DevelopmentCredentials.cs`），无法用环境变量修改，也不校验真实用户目录。

| 环境变量 | 默认 | 说明 |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT=Development` | `Production` | **前置条件**，handler 与校验器双重限制 |
| `Authentication__Mode=Basic` | Development json 已默认 | 无必填子项 |
| `Authentication__EnableKeycloak=false` | `true` | 可选：隐藏前端 OIDC 登录入口，只留账号密码表单 |
| `Authentication__AllowDevelopmentAnonymous` | 代码 `true` / json 与 Compose `false` | `true` 时无凭据请求认证为 `development-user`；`false` 时必须带 Basic 头 |
| `Authentication__DevelopmentUserId` / `__DevelopmentTenantId` | `development-user` / `development` | 匿名身份与租户 |

Basic 模式下 `POST /api/v1/auth/password/token`（仅 Development 映射）返回 `base64(user:pass)` 供前端当 AccessToken 使用。

本地 Docker 联调切 Basic（`deploy/.env`）：

```dotenv
OPENAGENT_ASPNETCORE_ENVIRONMENT=Development
OPENAGENT_AUTH_MODE=Basic
OPENAGENT_AUTH_ENABLE_KEYCLOAK=false
OPENAGENT_AUTH_ALLOW_DEVELOPMENT_ANONYMOUS=false
```

## 5. .NET 配置环境变量参考

以下变量对 Engine.Host（E）、Router（R）、Runner（u）生效，未标注则适用于 E+R。默认值指当前 `appsettings.json` / 选项类的生效值。

### 5.1 ASP.NET Core 宿主

| 变量 | 适用 | 默认 | 作用 / 缺失影响 |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | E/R/u | `Production`（应用 Compose 兜底 `Development`） | 选择 appsettings 覆盖文件；门控 Basic 认证、启动迁移、管理端点、Swagger |
| `ASPNETCORE_URLS` | E/R/u | 镜像 Dockerfile 固定：engine `http://+:5208`、router `http://+:5001`、runner `http://+:5209` | Kestrel 绑定；也用于心跳端口探测 |
| `ASPNETCORE_HTTP_PORTS` | E | — | 心跳端口探测优先于此变量 |
| `DOTNET_ENVIRONMENT` | E/R/u | — | `ASPNETCORE_ENVIRONMENT` 缺失时的回退 |
| `AllowedHosts` | E/R | `*` | 主机过滤 |
| `Cors__AllowedOrigins__0..n` | E/R | `http://localhost:5173` | CORS 白名单（应用 Compose 注入 Chat 公开地址） |

### 5.2 数据库与存储（Engine）

实现：`Backend/src/OpenAgent.Framework/ServiceCollectionExtensions.cs`。

| 变量 | 默认 | 必填 | 作用 / 缺失影响 |
|---|---|---|---|
| `ConnectionStrings__OpenAgentDatabase` | json 兜底 `Host=localhost;...;Password=openagent` | 生产必填 | EF Core / Npgsql 主库连接串。配置源完全没有该键时启动抛异常；不覆盖默认值则连本机库 |
| `Storage__Provider` | `PostgreSql` | 否 | 目前仅支持 PostgreSql，其他值启动抛 `NotSupportedException` |
| `Storage__ConnectionStringName` | `OpenAgentDatabase` | 否 | 使用哪个连接串键 |
| `Database__ApplyMigrationsOnStartup` | `false`（应用 Compose `true`） | 否 | **仅 Development 生效**，启动时执行 EF 迁移 |

### 5.3 Redis 与缓存

| 变量 | 适用 | 默认 | 作用 / 缺失影响 |
|---|---|---|---|
| `ConnectionStrings__Redis` | E/R | 空（Development json 置空） | 否。Engine 缺失时降级：无会话热缓存、引擎注册表孤岛模式（Router 发现不到）、DataProtection 密钥不落 Redis；Router 缺失时回退静态路由表与内存缓存 |
| `ConversationCache__Enabled` | E | `true`（Development json `false`） | 会话 Redis 写穿缓存开关 |
| `ConversationCache__TimeToLiveMinutes` | E | `30` | 缓存 TTL（分钟） |
| `ConversationCache__ConnectionString` | E | 空 | 专用缓存连接串，优先于 `ConnectionStrings__Redis` |
| `ConfigurationStore__RedisCacheTtlSeconds` | E | `300` | Agent/LLM 配置的 Redis 缓存 TTL |

Redis 行为细节见 [Redis 集成](../integrations/redis-engine/README.md)。

### 5.4 认证（Engine 与 Router 共用）

完整清单与规则见第 3、4 节；实现：`Backend/src/OpenAgent.Engine/Hosting/Authentication/`。补充第 3、4 节未覆盖项：

| 变量 | 默认 | 作用 |
|---|---|---|
| `Authentication__EnableApiKey` | `false` | 开启后 `Authorization: Bearer` 非 JWT 时按第三方 API Key（数据库 `third_party_api_keys` 表）解析；Router 侧会调 Engine 换取身份，要求 Router 可达 Engine。见 [认证模块文档](../modules/security/auth/README.md) |

### 5.5 授权（Engine 能力域）

| 变量 | 默认 | 作用 / 缺失影响 |
|---|---|---|
| `Authorization__Mode` | 代码 `AllowAll`；Engine.Host 生产 json `Claims`、Development json `AllowAll` | `Claims`：拒绝未认证、`Admin` 角色全通、按 scope/permissions 匹配；`AllowAll`：全放行。见 [权限文档](../modules/security/permission/README.md) |

已认证用户访问 `/api/v1/agent`、`/api/v1/admin` 必须携带 `tenant_id`（或 `tid`）claim，缺失即 403；与 `X-Tenant-Id` 头冲突也 403。

### 5.6 OpenCode / ACP 运行时（Engine）

实现：`Backend/src/OpenAgent.Runtime/OpenCode/OpenCodeOptions.cs`、`OpenCodeProcess.cs`；注册 `Backend/src/OpenAgent.Runtime/RuntimeServiceExtensions.cs`。

| 变量 | 默认 | 作用 / 缺失影响 |
|---|---|---|
| `OpenCode__Executable` | `opencode`（容器镜像内嵌绝对路径） | 可执行名或绝对路径。找不到时**启动正常、首次聊天抛错**（DependencyUnavailable）；显式路径不存在为配置错误 |
| `OpenCode__Arguments__0..n` | `["acp"]` | 子进程参数 |
| `OpenCode__WorkspaceRoot` | `%TEMP%\openagent-acp` | ACP 会话工作区根，必须绝对路径，否则启动校验失败 |
| `OpenCode__RequestTimeoutSeconds` | `30` | 单 JSON-RPC 请求超时（1..3600） |
| `OpenCode__TurnTimeoutSeconds` | `1800` | 单轮对话超时（1..86400） |
| `OpenCode__MaxConcurrentTurns` | `8`（1..128） | 并发轮上限 |
| `OpenCode__MaxPromptCharacters` | `2097152` | 提示词长度上限 |
| `OpenCode__LogUsageUpdates` | `false` | usage_update 事件日志开关 |
| `OpenCode__KeepAliveSeconds` | `0`（关闭） | 空闲进程保活复用窗口 |
| `OpenCode__MaxKeptProcesses` | `8` | 保活进程池上限 |
| `OpenCode__MaxSessionsPerProcess` | `64` | 单进程会话数上限 |
| `OpenCode__AllowedNativeTools__0..n` | 空（Development json：`read,edit,write,bash`） | 放行给模型的 OpenCode 原生工具名 |

宿主会向 OpenCode 子进程白名单透传 `NODE_TLS_REJECT_UNAUTHORIZED`、`NODE_EXTRA_CA_CERTS`（自签模型端点场景）及 `PATH`/`PATHEXT`/`COMSPEC`/`SystemRoot`/`WINDIR`。`OPENAGENT_MODEL_API_KEY`、`OPENCODE_CONFIG_CONTENT` 等由代码注入子进程，不是用户配置项。详见 [OpenCode 运行时](../integrations/opencode-runtime.md)。

### 5.7 代码执行 / Runner

Runner 服务自身（实现：`Backend/src/OpenAgent.Runner/RunnerOptions.cs`、校验 `Program.cs`）：

| 变量 | 默认 | 必填 | 作用 |
|---|---|---|---|
| `Runner__ApiKey` | 空 | **必填**（≥32 字符，`ValidateOnStart` 失败即进程崩溃） | Runner 全部 `/api/v1/*` 的 Bearer 鉴权 |
| `Runner__WorkspaceRoot` | `/var/lib/openagent-runner/workspaces` | 须专用绝对目录 | 会话工作区根 |
| `Runner__BubblewrapPath` | `/usr/bin/bwrap` | 否 | 沙箱可执行（绝对路径） |
| `Runner__PythonPath` / `Runner__NodePath` | `/opt/openagent-code/venv/bin/python` / `/usr/bin/node` | 否 | 沙箱内解释器 |
| `Runner__TimeoutSeconds` | `120` | 否 | 单次执行超时 |
| `Runner__MaxConcurrentExecutions` | `2` | 否 | 并发执行上限 |
| `Runner__MemoryMiB` / `Runner__WorkspaceMiB` / `Runner__MaxProcesses` | `1536` / `128` / `64` | 否 | 每沙箱资源限额 |
| `Runner__SessionIdleMinutes` / `Runner__MaxSessionSandboxes` | `120` / `64` | 否 | 会话回收与沙箱池上限 |

Engine 侧调用 Runner（`Backend/src/OpenAgent.Capabilities/Extensions/RunnerServiceExtensions.cs`）：

| 变量 | 默认 | 必填 | 作用 / 缺失影响 |
|---|---|---|---|
| `CodeExecution__Enabled` | `false`（应用 Compose `true`） | 否 | 总开关；false 时 execute_code / run_skill_script 能力禁用 |
| `CodeExecution__Endpoint` | 空 | **`Enabled=true` 时必填**（绝对 http/https） | Runner 地址（如 `http://runner:5209`），缺失启动失败 |
| `CodeExecution__ApiKey` | 空 | **`Enabled=true` 时必填**（≥32 字符，与 Runner 共享） | 服务间鉴权，缺失启动失败 |
| `CodeExecution__RequestTimeoutSeconds` | `180`（10..900） | 否 | 请求超时 |

### 5.8 文件资产 / 对象存储（Engine）

实现：`Backend/src/OpenAgent.Capabilities/Extensions/FileAssetServiceExtensions.cs` 等。

| 变量 | 默认 | 必填 | 作用 |
|---|---|---|---|
| `FileAssets__Enabled` | 生产 json `false` / Development json 与 Compose `true` | 否 | 上传、分享、内联图片总开关 |
| `FileAssets__ObjectStorage__BucketName` | `openagent-files` | Enabled 时非空必填 | S3 桶名 |
| `FileAssets__ObjectStorage__Region` | `us-east-1` | Enabled 时非空必填 | 区域（MinIO 任意非空） |
| `FileAssets__ObjectStorage__KeyPrefix` | `files` | 须安全路径段 | 对象键前缀 |
| `FileAssets__ObjectStorage__ServiceUrl` | `null` | 否 | 自建/MinIO 端点；`null` 走 AWS 默认端点与凭证链 |
| `FileAssets__ObjectStorage__ForcePathStyle` | `false` | 否 | MinIO 需 `true` |
| `FileAssets__ObjectStorage__AccessKey` / `__SecretKey` | `null` | 否，但**必须成对** | 静态凭证；缺省走 AWS 默认凭证链 |
| `FileAssets__Share__PublicBaseUrl` | `null` | 否 | 分享链接公网基地址 |
| `FileAssets__Share__TemporaryLifetimeSeconds` 等 4 个生命周期键 | `900` / `86400` / `2592000` / `259200` | 否（≤365 天） | 分享链接有效期 |
| `FileAssets__MaxFileSizeBytes` / `__MaxFileCount` | `104857600` / `10` | 否 | 单文件 100 MB、单次附件数上限 |
| `FileAssets__MaxFunctionReadBytes` | `131072` | 否 | 工具读文件字节上限 |
| `FileAssets__MaxInlineImageBytes` / `__MaxInlineImageCount` / `__InlineImageHistoryTurns` | `4194304` / `4` / `2` | 否 | 内联图片限额 |
| `LocalDevelopment__FileStorageRoot` | `%LOCALAPPDATA%\OpenAgent\objects` | 否 | 仅 Development 的本地对象存储根 |

详见 [文件资产](../integrations/file-assets.md)。

### 5.9 遥测与日志（E/R）

| 变量 | 默认 | 作用 / 缺失影响 |
|---|---|---|
| `OpenTelemetry__OtlpEndpoint` | 空 | OTLP 导出地址；空时回退 `OTEL_EXPORTER_OTLP_ENDPOINT`，两者都空则禁用 OTLP 导出（Prometheus `/metrics` 仍可用）；**设置了但非绝对 HTTP(S) URI → 启动抛异常** |
| `OpenTelemetry__ServiceName` | `openagent-engine` / `openagent-router` | OTel 资源名 |
| `OpenTelemetry__ServiceVersion` | `1.0.0`（Compose 注入构建版本） | OTel 资源版本 |
| `Serilog__MinimumLevel__Default` | `Information`（Development `Debug`） | 日志级别，任意 Serilog 节均可 env 覆盖（如 `Serilog__MinimumLevel__Override__Microsoft.AspNetCore=Debug`） |

### 5.10 LLM 与会话行为（Engine）

| 变量 | 默认 | 作用 |
|---|---|---|
| `Llm__NetworkTimeoutSeconds` | `900`（0=不限） | 模型 SDK 网络超时（适配推理模型长停顿） |
| `LlmInteraction__Enabled` / `__MaxContentLength` | `true` / `100000` | LLM 交互日志（落 PostgreSQL）及单字段截断 |
| `ConversationStore__DefaultModelContextTokens` / `__TitleTruncateLength` / `__EnableAutoCompaction` | `1000` / `50` / `false` | 上下文窗口回退值、标题截取、自动压缩 |
| `AgentExecution__ToolCallTimeoutSeconds` | `300` | 工具调用超时 |
| `AgentExecution__ReadToolResultCharBudget` / `__ExecutionToolResultCharBudget` / `__DefaultToolResultCharBudget` | `80000` / `40000` / `60000` | 工具结果字符预算 |

模型 Endpoint / ApiKey 本身存数据库（`LlmConfig`），**不是环境变量**；录入界面见管理端点。

### 5.11 心跳与停机（Engine）

| 变量 | 默认 | 作用 |
|---|---|---|
| `Heartbeat__IntervalSeconds` / `__RetryDelaySeconds` / `RegistryTtlSeconds` | `10` / `5` / `30` | 引擎注册心跳周期与 TTL |
| `Heartbeat__AdvertisedHost` / `__AdvertisedPort` | 机器名 / 自动探测（`ASPNETCORE_HTTP_PORTS`→`ASPNETCORE_URLS`→80） | 通告给 Router 的地址（Compose 设 `engine` / `5208`） |
| `Heartbeat__Intents__0..n` | `["chat"]` | 通告能力意图 |
| `Shutdown__TimeoutSeconds` | `30` | 优雅停机宽限 |

### 5.12 Router 专属

| 变量 | 默认 | 作用 |
|---|---|---|
| `RouterSettings__Routing__EngineEndpoint` | `http://localhost:5208`（Compose `http://engine:5208`） | 静态兜底的 Engine 地址 |
| `RouterSettings__Routing__WorkflowEndpoint` | `http://localhost:5003` | workflow 意图路由目标 |
| `RouterSettings__Caching__MaxRequestBodyBytes` / `__MaxResponseBodyBytes` | `1048576` / `4194304` | 幂等/查询缓存体上限 |
| `RouterSettings__Caching__IdempotencyTtlSeconds` / `__IdempotencyPendingTtlSeconds` / `__QueryTtlSeconds` | `86400` / `120` / `300` | 缓存 TTL |
| `RouterSettings__RateLimiting__RequestsPerSecond` / `__BurstCapacity` / `__FailureMode` | `100` / `200` / `Local` | 令牌桶限流 |
| `RouterSettings__ServiceDiscovery__RefreshIntervalSeconds` / `__HeartbeatStaleAfterSeconds` / `__SnapshotMaxAgeSeconds` / `__RedisFailureMode` | `5` / `60` / `15` / `StaticOnly` | Redis 服务发现 |
| `RouterSettings__IntentRecognition__Enabled` / `__TimeoutMs` / `__MinimumConfidence` | `true` / `5000`（Compose `30000`）/ `0.5` | 意图识别路由 |
| `RouterSettings__AgentProviders__DefaultProviderId` 等 | appsettings.json 提供 `self-engine` | Provider 注册表；结构无效时 Router 启动失败（默认配置已满足，一般无需 env） |

### 5.13 其他

| 变量 | 默认 | 作用 |
|---|---|---|
| `OPENAGENT_ALLOW_INSECURE_TLS` | `false` | 部署逃生门：跳过 LLM、S3、MCP、RAG、Runner、OIDC 回调等出站请求的 TLS 证书校验。仅自签证书受控场景使用，生产优先安装内部 CA |
| `Secrets__{tenantId}__{name}` | 无 | 租户密钥的配置引用形式（`Backend/src/OpenAgent.Engine/Config/ConfigurationSecretResolver.cs`） |

## 6. Compose 部署变量（`OPENAGENT_*`）

这一层变量只在 `deploy/` 的 Compose 文件与脚本中插值（`.env` 文件），由 Compose 转换成第 5 节的 `节__键` 注入容器，或供基础设施容器自身使用。模板见 `deploy/deployment.env.example`。

### 6.1 认证与 Keycloak

| 变量 | 默认 | 注入为 / 用途 |
|---|---|---|
| `OPENAGENT_AUTH_MODE` | `JwtBearer` | engine/router 的 `Authentication__Mode` |
| `OPENAGENT_AUTH_ENABLE_KEYCLOAK` / `OPENAGENT_AUTH_ENABLE_API_KEY` | `true` / `false` | 对应 `Authentication__Enable*` |
| `OPENAGENT_AUTH_ALLOW_DEVELOPMENT_ANONYMOUS` | `false` | `Authentication__AllowDevelopmentAnonymous` |
| `OPENAGENT_AUTH_AUDIENCE` / `OPENAGENT_AUTH_CLIENT_ID` | `openagent-api` / `openagent-chat` | `Authentication__Audience` / `ClientId` |
| `OPENAGENT_AUTH_REQUIRE_HTTPS_METADATA` / `OPENAGENT_AUTH_CLOCK_SKEW_SECONDS` | `false` / `60` | 对应 `Authentication__*` |
| `OPENAGENT_KEYCLOAK_PUBLIC_URL` | 由 `OPENAGENT_PUBLIC_HOST`+`OPENAGENT_KEYCLOAK_PORT` 派生 | 双重用途：派生 `Authentication__Authority` 与 Keycloak 容器 `KC_HOSTNAME` |
| `OPENAGENT_KEYCLOAK_METADATA_ADDRESS` | `http://keycloak:8080/realms/openagent/.well-known/openid-configuration` | `Authentication__MetadataAddress` |
| `OPENAGENT_KEYCLOAK_ADMIN_USERNAME` / `OPENAGENT_KEYCLOAK_ADMIN_PASSWORD` | `admin` / `admin` | Keycloak 容器 `KC_BOOTSTRAP_ADMIN_*`，生产必改 |
| `OPENAGENT_KEYCLOAK_COMMAND` | `start --import-realm` | Keycloak 启动命令；生产用 `start`（首次导入后） |
| `OPENAGENT_KEYCLOAK_BACKCHANNEL_DYNAMIC` / `OPENAGENT_KEYCLOAK_HTTP_ENABLED` | `true` / `true` | Keycloak 容器 `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` / `KC_HTTP_ENABLED` |

### 6.2 数据库 / Redis / S3

| 变量 | 默认 | 用途 |
|---|---|---|
| `OPENAGENT_POSTGRES_DB` / `__USER` / `__PASSWORD` | `openagent` / `openagent` / `openagent` | 同时拼进 engine 连接串与 PostgreSQL 容器 `POSTGRES_*`；生产必改密码 |
| `OPENAGENT_POSTGRES_HOST` / `__INTERNAL_PORT` | `postgres` / `5432` | engine 连接串的目标地址（容器网络内） |
| `OPENAGENT_POSTGRES_PORT` | `5432` | PostgreSQL 宿主机发布端口 |
| `OPENAGENT_REDIS_HOST` / `__INTERNAL_PORT` | `redis` / `6379` | engine/router 的 `ConnectionStrings__Redis` |
| `OPENAGENT_REDIS_PORT` | `6379` | Redis 宿主机发布端口 |
| `OPENAGENT_S3_SERVICE_URL` / `OPENAGENT_S3_BUCKET` / `OPENAGENT_S3_ACCESS_KEY` / `OPENAGENT_S3_SECRET_KEY` | `http://minio:9000` / `openagent-files` / `openagent` / `openagent-secret` | engine 的 `FileAssets__ObjectStorage__*`；AK/SK 同时是 MinIO 容器 `MINIO_ROOT_*` |
| `OPENAGENT_MINIO_PORT` / `OPENAGENT_MINIO_CONSOLE_PORT` | `9000` / `9001` | MinIO 宿主机发布端口 |
| `OPENAGENT_FILE_ASSETS_ENABLED` | `true` | engine 的 `FileAssets__Enabled` |
| `OPENAGENT_SHARE_PUBLIC_BASE_URL` | 空 | engine 的 `FileAssets__Share__PublicBaseUrl` |
| `OPENAGENT_RUNNER_API_KEY` | **无（必填）** | engine 的 `CodeExecution__ApiKey` 与 runner 的 `Runner__ApiKey`；Compose 用 `:?` 语法强制要求，缺失直接拒绝部署 |

### 6.3 地址 / 端口 / 网络 / TLS

| 变量 | 默认 | 用途 |
|---|---|---|
| `OPENAGENT_PUBLIC_HOST` / `OPENAGENT_PUBLIC_SCHEME` | `localhost` / `https` | 派生所有公开 URL（Chat、Keycloak、CORS、前端构建参数） |
| `OPENAGENT_CHAT_PORT` / `OPENAGENT_ROUTER_PORT` / `OPENAGENT_ENGINE_PORT` / `OPENAGENT_KEYCLOAK_PORT` | `8081` / `8082` / `8083` / `58081` | Nginx HTTPS 发布端口；Keycloak 8443 的映射端口 |
| `OPENAGENT_CHAT_PUBLIC_URL` | 由 scheme+host+chat 端口派生 | 同时用于 CORS、Keycloak 回调与 Web Origin、Chat 镜像构建 |
| `OPENAGENT_COMPOSE_PROJECT` / `OPENAGENT_APP_NETWORK` / `OPENAGENT_INFRA_PROJECT` / `OPENAGENT_INFRA_NETWORK` | `openagent-app` / `openagent` / `openagent-infrastructure` / `openagent-infrastructure` | Compose 项目名与网络名；迁移已有部署必须保持 |
| `OPENAGENT_NGINX_CLIENT_MAX_BODY_SIZE` | `1100m` | Nginx 上传体积限制 |
| `OPENAGENT_TLS_CERT_DIR` | `deploy/openagent/nginx/certs` | 证书目录（Nginx 与 Keycloak 共用） |

### 6.4 镜像 / 构建 / 脚本

| 变量 | 默认 | 用途 |
|---|---|---|
| `OPENAGENT_ENGINE_IMAGE` / `OPENAGENT_ROUTER_IMAGE` / `OPENAGENT_CHAT_IMAGE` / `OPENAGENT_RUNNER_IMAGE` | `openagent-*:latest` | 各服务镜像标签 |
| `OPENAGENT_SERVICE_VERSION` | `development` | 注入 `OpenTelemetry__ServiceVersion` |
| `OPENAGENT_OTLP_ENDPOINT` | 空（禁用） | engine/router 的 `OpenTelemetry__OtlpEndpoint` |
| `OPENAGENT_ALLOW_INSECURE_TLS` | `false` | 原名注入 engine/router |
| `OPENAGENT_DOCKER_MODE` | `auto` | 构建部署脚本选择 docker / wsl-docker |
| `OPENAGENT_IMAGE_TAR_DIR` | 空 | 镜像 TAR 导出目录 |
| `OPENAGENT_OPENCODE_VERSION` | `Backend/opencode.version` 锁定值 | 仅升级前临时覆盖验证 |
| `OPENAGENT_TENANT_ID` | `development` | Chat 镜像构建参数（→ 前端 `VITE_OPENAGENT_TENANT_ID`） |

### 6.5 无效 / 遗留变量（避免踩坑）

以下键在部署文件中出现过但当前代码**不读取**，设置无效：

- `Engine__AllowMockAgent`（`deploy/openagent/preview.compose.yml` 遗留）；
- `Engine__AdvertisedHost` / `Engine__AdvertisedPort`（Engine.Host 的 `appsettings.Development.json` 遗留；现行键是 `Heartbeat__AdvertisedHost` / `Heartbeat__AdvertisedPort`）。

## 7. 前端变量

前端构建时内联 `VITE_*`（浏览器无法读取运行时环境变量），定义在 `Frontend/OpenAgent.Chat/Dockerfile` 与 `Frontend/OpenAgent.Chat/src/shared/api/http.ts`：

| 变量 | 来源 | 默认 | 作用 |
|---|---|---|---|
| `OPENAGENT_PUBLIC_SCHEME` / `OPENAGENT_PUBLIC_HOST` / `OPENAGENT_ROUTER_PORT` / `OPENAGENT_ENGINE_PORT` | Dockerfile 构建参数 | `https` / `localhost` / `8082` / `8083` | 镜像内拼出下面两个 `VITE_*` |
| `OPENAGENT_TENANT_ID` | Dockerfile 构建参数 | `development` | `VITE_OPENAGENT_TENANT_ID` |
| `VITE_OPENAGENT_ROUTER_BASE_URL` | 构建注入 / 本地 dev server | `https://{host}:{router端口}` | Router API 基地址 |
| `VITE_OPENAGENT_ENGINE_BASE_URL` | 构建注入 / 本地 dev server | `https://{host}:{engine端口}` | 直连 Engine 模式的基地址 |
| `VITE_OPENAGENT_TENANT_ID` | 构建注入 / 本地 dev server | `development` | 默认租户 |
| `VITE_OPENAGENT_CONNECTION_MODE` | 本地 dev server | `router` | 默认连接模式（`engine` / `router`） |

认证参数（authority、clientId、scopes、模式）统一由后端 `GET /api/v1/auth/config` 下发，前端没有对应环境变量。

## 8. 必填与缺失影响总览

| 变量 | 适用场景 | 缺失 / 非法后果 |
|---|---|---|
| `OPENAGENT_RUNNER_API_KEY` → `Runner__ApiKey` / `CodeExecution__ApiKey` | 部署 Runner（应用 Compose 默认启用） | **Compose 拒绝部署**；裸机 Runner 启动崩溃；Engine 侧 `CodeExecution__Enabled=true` 时启动失败 |
| `Authentication__Authority` / `__Audience` / `__ClientId` | `Mode=JwtBearer`（生产默认） | Engine 与 Router **启动失败**；生产 json 三者为空，必须经 `.env` 或直接环境变量提供 |
| `Authentication__Mode=Basic` 且非 Development | — | 启动失败（Basic 仅限 Development） |
| `RequireHttpsMetadata=false` 且（非 Development 或 Authority 非 localhost） | 本地 HTTP 联调外 | 启动失败 |
| `ConnectionStrings__OpenAgentDatabase` | Engine | 键完全缺失 → 启动异常；保留默认值 → 只连本机库（生产不可用） |
| `CodeExecution__Endpoint` / `__ApiKey` | `CodeExecution__Enabled=true` | 启动失败 |
| `FileAssets__ObjectStorage__*` | `FileAssets__Enabled=true` | 桶名/区域为空或凭证不成对 → 启动失败（默认值已满足） |
| `OpenTelemetry__OtlpEndpoint` | 可选 | 不设 → 禁用 OTLP 导出（可运行）；设了但 URI 非法 → 启动异常 |
| `OpenCode__Executable` / `PATH` 中的 opencode | 聊天功能 | 启动正常，**首次聊天报错**（DependencyUnavailable） |
| `ConnectionStrings__Redis` | 可选 | Engine/Router 降级运行（无会话缓存、静态路由、内存缓存） |
| `OPENAGENT_POSTGRES_PASSWORD` / MinIO 与 Keycloak 管理员凭据 | 生产 | 不改默认凭据 = 安全风险（功能不中断） |

## 9. 相关文档

- [部署指南](deployment.md)：Compose 启动、TLS 证书、数据卷与镜像更新流程
- [Keycloak 本地认证集成](../integrations/keycloak/README.md)：Realm 导入、用户与租户创建、验证命令
- [认证模块文档](../modules/security/auth/README.md)：认证方案行为矩阵与源码索引
- [Runner 部署](../integrations/code-runner.md)、[文件资产](../integrations/file-assets.md)、[OpenCode 运行时](../integrations/opencode-runtime.md)、[可观测性](../overview/Observability.md)
