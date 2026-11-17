# 环境变量指南

本文件是 OpenAgent 环境变量的唯一事实源：覆盖 Engine.Host、Router 两个宿主实际读取的全部配置环境变量、按服务加载的 Compose 环境文件，以及不同场景的最小配置集。部署流程本身见 [部署指南](deployment.md)。

## 1. 配置加载规则

两个宿主均使用 `WebApplication.CreateBuilder` 默认配置管线（`Backend/src/OpenAgent.Engine.Host/Program.cs`、`Backend/src/OpenAgent.Router/Program.cs`），**没有应用级环境变量前缀**：

- 优先级从低到高：`appsettings.json` → `appsettings.{Environment}.json` → User Secrets（仅 Development）→ **全部环境变量** → 命令令行。
- 环境变量用双下划线 `__` 表示配置层级，等价于 `:`。例如 `Authentication__Mode` 覆盖 `appsettings.json` 的 `Authentication:Mode`。数组用 `节__键__0`、`节__键__1` 形式。
- 不存在 `OPENAGENT__SECTION__KEY` 之类的应用前缀；`deploy/` 中应用参数直接填写原生变量，Nginx 模板变量单独放在入口配置（见第 6 节）。

少数变量绕过 IConfiguration 直读：`OTEL_EXPORTER_OTLP_ENDPOINT`（OTLP 回退地址）、`ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS`（心跳端口探测）、`PATH` / `PATHEXT` 等（OpenCode 子进程可执行解析与最小环境透传）。

## 2. 场景速查

| 场景 | 需要设置的环境变量 |
|---|---|
| 本地裸机开发（`dotnet run`） | **零配置**。Development 配置默认 Basic 匿名登录、连接本机 PostgreSQL（`localhost:5432`，账号 openagent/openagent）。前置条件：本机可用的 PostgreSQL 与 `opencode` 可执行；否则见第 5.2 / 5.6 节覆盖 |
| 本地 Docker 全栈 | 运行 `scripts/deploy.sh init`，填写 Engine 连接/凭据及共享运行配置；当前 dev 部署选择 Production + JWT/Keycloak |
| 内网/生产部署 | 填写应用共享与各服务 `.env`；端口和运行参数可配置，项目/网络/镜像名称固定，见第 6 节 |
| 启用代码执行 | 平台无独立沙箱：经 `OpenCode__AllowedNativeTools__0..n` 放行 OpenCode 原生工具（如 `read`/`edit`/`write`/`bash`），见第 5.6 节与 [OpenCode 运行时](../integrations/opencode-runtime.md) |
| 外接 S3/MinIO | `deploy/openagent/engine/.env` 中的 `FileAssets__ObjectStorage__ServiceUrl`、`BucketName`、`AccessKey`、`SecretKey` |
| 接入日志中心 | `.env` 设置 `OPENAGENT_OTLP_ENDPOINT`（OTLP gRPC/HTTP 地址）；规则见 [可观测性](../overview/Observability.md) |
| 自签证书内网 | `OPENAGENT_ALLOW_INSECURE_TLS=true`（逃生门，默认关闭；详见第 5.12 节） |

## 3. 启用 Keycloak（JwtBearer）

Engine 与 Router 共用同一套认证（都经 `AddAgentHost` 组合，`Backend/src/OpenAgent.Engine/Hosting/ServiceCollectionExtensions.cs`），**两个服务的变量必须设置一致**；应用 Compose 已把同一组 `Authentication__*` 同时注入 engine 与 router。

### 3.1 需要改动的变量

以下参数直接填写在 `deploy/openagent/.env`，Engine / Router 共用，不经过包装变量。

| 原生变量 | 要求 | 默认来源与示例部署值 | 说明 |
|---|---|---|---|
| `Authentication__Mode` | 部署选择 | 应用默认 Basic；本部署示例显式 JwtBearer | 选择认证模式 |
| `Authentication__Authority` | JWT 必填 | 应用为空；部署文件直接填写完整 Realm URL | OIDC 签发方 |
| `Authentication__Audience` / `ClientId` | JWT 必填 | 部署示例 openagent-api / openagent-chat | API audience 与 SPA client |
| `Authentication__MetadataAddress` | 可选 | 应用无固定地址；本部署填写内部 HTTPS discovery | 服务端发现地址，与公开 Issuer 分开配置 |
| `Authentication__RequireHttpsMetadata` | 可选 | 默认 true | 生产 metadata 必须 HTTPS |
| `Authentication__ClockSkewSeconds` | 可选 | 默认 60（0..300） | JWT 时钟容差 |
| `Authentication__Scopes__0..n` | 可选 | json：openid、profile、email、organization | 下发给前端的 OIDC scopes |
| `Authentication__EnableKeycloak` | 可选 | 应用默认 false；本部署示例显式 true | 显示 OIDC 登录入口 |
| `Authentication__EnableApiKey` | 可选 | 默认 false | 叠加第三方 API Key 认证 |

**校验规则**（`Backend/src/OpenAgent.Engine/Hosting/Authentication/AgentAuthenticationOptionsValidator.cs`，启动即校验、失败拒绝启动）：

- `Mode=JwtBearer` 时 `Authority`、`Audience`、`ClientId` 三者缺一即启动失败；
- Authority 必须是绝对 HTTP(S) URI；`RequireHttpsMetadata=true` 时 Authority 必须 HTTPS；
- `RequireHttpsMetadata=false` 仅允许 Development 环境 + localhost Authority（本地 Docker 联调可用；生产置 false 会启动失败）；
- 非 Development 环境 `MetadataAddress` 必须 HTTPS。

### 3.2 Keycloak 侧需要准备

基础设施 Compose（`deploy/infrastructure/docker-compose.yml`）首次启动自动导入 Realm（`deploy/infrastructure/keycloak/realm/openagent-realm.json`）：Realm `openagent`、公开 SPA Client `openagent-chat`（Authorization Code + PKCE）、audience mapper（把 `openagent-api` 写入 access token）、`tenant_id`/`roles` claim mapper。**用户和租户组织需在管理台手动创建**，且用户属性 `tenant_id` 必须与所属 Organization 一致，否则已登录请求会因缺租户 claim 被拒。完整步骤见 [Keycloak 本地认证集成](../integrations/keycloak.md)。

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

Compose 部署按应用 Compose 中的 OIDC 注释启用（取消 `Authentication__Mode=JwtBearer` 等注释行并启动 `keycloak` profile），公开地址按基础设施 Compose 注释覆盖；Engine 与 Router 必须使用同一组参数。

前端不需要任何认证环境变量：OIDC 参数由 `GET /api/v1/auth/config` 下发。

## 4. Basic 认证（应用默认，可按部署显式启用）

Basic 是部署默认认证方式，任意环境可用；Production 下必须配置密码（`Authentication__BasicPassword`，缺失即启动失败），用户名默认 `admin`、租户默认 `development`，Engine 与 Router 共用。**配置密码后内置开发账号（`admin/admin`、`test/test`）即失效**；仅 Development 且未配置密码时才回退到内置账号（`Backend/src/OpenAgent.Engine/Hosting/Authentication/DevelopmentCredentials.cs`，固定时间比较）。

| 环境变量 | 默认 | 说明 |
|---|---|---|
| `Authentication__Mode=Basic` | 部署默认 | Production 可用，无需 Development |
| `Authentication__BasicPassword` | 空 | **Production 必填**（缺失启动失败）；直接填写共享文件中的原生字段 |
| `Authentication__BasicUsername` / `__BasicTenantId` | `admin` / `development` | Engine 与 Router 必须一致；含 `:` 的用户名被拒绝 |
| `Authentication__EnableKeycloak=false` | `false` | 隐藏前端 OIDC 登录入口，只留账号密码表单 |
| `Authentication__AllowDevelopmentAnonymous` | 默认 false；Development 可另行覆盖 | `true` 时无凭据请求认证为 `development-user`；`false` 时必须带 Basic 头 |
| `Authentication__DevelopmentUserId` / `__DevelopmentTenantId` | `development-user` / `development` | 匿名身份与租户 |

Basic 模式下 `POST /api/v1/auth/password/token`（`Mode=Basic` 时映射）返回 `base64(user:pass)` 供前端当 AccessToken 使用。

本部署示例使用 JWT。如需 Basic，在共享 `.env` 中显式设置 `Authentication__Mode=Basic`、`Authentication__EnableKeycloak=false` 和 `Authentication__BasicPassword`；匿名访问默认关闭。

## 5. .NET 配置环境变量参考

以下变量对 Engine.Host（E）、Router（R）生效，未标注则适用于 E+R。默认值指当前 `appsettings.json` / 选项类的生效值。

### 5.1 ASP.NET Core 宿主

| 变量 | 适用 | 默认 | 作用 / 缺失影响 |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | E/R | `Production`（应用 Compose 同为 `Production`；预览栈用 `Development`） | 选择 appsettings 覆盖文件；门控管理端点、Swagger。Basic 认证与启动迁移（`Database__ApplyMigrationsOnStartup`）在任意环境均按开关生效 |
| `ASPNETCORE_URLS` | E/R | 镜像 Dockerfile 固定：engine `http://+:5208`、router `http://+:5001` | Kestrel 绑定；也用于心跳端口探测 |
| `ASPNETCORE_HTTP_PORTS` | E | — | 心跳端口探测优先于此变量 |
| `DOTNET_ENVIRONMENT` | E/R | — | `ASPNETCORE_ENVIRONMENT` 缺失时的回退 |
| `AllowedHosts` | E/R | `*` | 主机过滤 |
| `Cors__AllowedOrigins__0..n` | E/R | `*` | 默认允许任意来源、方法和请求头；主/预览 Compose 不覆盖。通配模式不启用跨域 Cookie，支持 Authorization 头；如需白名单，用完整来源地址替换 `*`（协议、主机、端口，无路径和末尾斜杠），白名单来源沿用允许 credentials 的行为 |

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

Redis 行为细节见 [Redis 集成](../integrations/redis-engine.md)。

### 5.4 认证（Engine 与 Router 共用）

完整清单与规则见第 3、4 节；实现：`Backend/src/OpenAgent.Engine/Hosting/Authentication/`。补充第 3、4 节未覆盖项：

| 变量 | 默认 | 作用 |
|---|---|---|
| `Authentication__EnableApiKey` | `false` | 开启后 `Authorization: Bearer` 非 JWT 时按第三方 API Key（数据库 `third_party_api_keys` 表）解析；Router 侧会调 Engine 换取身份，要求 Router 可达 Engine。见 [认证模块文档](../modules/security.md) |

### 5.5 授权（Engine 能力域）

| 变量 | 默认 | 作用 / 缺失影响 |
|---|---|---|
| `Authorization__Mode` | 代码 `AllowAll`；Engine.Host 生产 json `Claims`、Development json `AllowAll` | `Claims`：拒绝未认证、`Admin` 角色全通、按 scope/permissions 匹配；`AllowAll`：全放行。见 [权限文档](../modules/security.md) |

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
| `OpenCode__AllowedNativeTools__0..n` | 空（Development json：`read,edit,write,bash`） | 放行给模型的 OpenCode 原生工具名。平台已无独立代码执行沙箱：需要模型执行代码时须在此放行（如 `bash`），放行即把对应主机读写/执行面交给 harness 进程 |

宿主会向 OpenCode 子进程白名单透传 `NODE_TLS_REJECT_UNAUTHORIZED`、`NODE_EXTRA_CA_CERTS`（自签模型端点场景）及 `PATH`/`PATHEXT`/`COMSPEC`/`SystemRoot`/`WINDIR`。`OPENAGENT_MODEL_API_KEY`、`OPENCODE_CONFIG_CONTENT` 等由代码注入子进程，不是用户配置项。详见 [OpenCode 运行时](../integrations/opencode-runtime.md)。

### 5.7 文件资产 / 对象存储（Engine）

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

### 5.8 遥测与日志（E/R）

| 变量 | 默认 | 作用 / 缺失影响 |
|---|---|---|
| `OpenTelemetry__OtlpEndpoint` | 空 | OTLP 导出地址；空时回退 `OTEL_EXPORTER_OTLP_ENDPOINT`，两者都空则禁用 OTLP 导出（Prometheus `/metrics` 仍可用）；**设置了但非绝对 HTTP(S) URI → 启动抛异常** |
| `OpenTelemetry__ServiceName` | `openagent-engine` / `openagent-router` | OTel 资源名 |
| `OpenTelemetry__ServiceVersion` | `1.0.0`（Compose 注入构建版本） | OTel 资源版本 |
| `Serilog__MinimumLevel__Default` | `Information`（Development `Debug`） | 日志级别，任意 Serilog 节均可 env 覆盖（如 `Serilog__MinimumLevel__Override__Microsoft.AspNetCore=Debug`） |

### 5.9 LLM 与会话行为（Engine）

| 变量 | 默认 | 作用 |
|---|---|---|
| `Llm__NetworkTimeoutSeconds` | `900`（0=不限） | 模型 SDK 网络超时（适配推理模型长停顿） |
| `LlmInteraction__Enabled` / `__MaxContentLength` | `true` / `100000` | LLM 交互日志（落 PostgreSQL）及单字段截断 |
| `ConversationStore__DefaultModelContextTokens` / `__TitleTruncateLength` / `__EnableAutoCompaction` | `1000` / `50` / `false` | 上下文窗口回退值、标题截取、自动压缩 |
| `AgentExecution__ToolCallTimeoutSeconds` | `300` | 工具调用超时 |
| `AgentExecution__ReadToolResultCharBudget` / `__ExecutionToolResultCharBudget` / `__DefaultToolResultCharBudget` | `80000` / `40000` / `60000` | 工具结果字符预算 |

模型 Endpoint / ApiKey 本身存数据库（`LlmConfig`），**不是环境变量**；录入界面见管理端点。

### 5.10 心跳与停机（Engine）

| 变量 | 默认 | 作用 |
|---|---|---|
| `Heartbeat__IntervalSeconds` / `__RetryDelaySeconds` / `RegistryTtlSeconds` | `10` / `5` / `30` | 引擎注册心跳周期与 TTL |
| `Heartbeat__AdvertisedHost` / `__AdvertisedPort` | 机器名 / 自动探测（`ASPNETCORE_HTTP_PORTS`→`ASPNETCORE_URLS`→80） | 通告给 Router 的地址（Compose 设 `engine` / `5208`） |
| `Heartbeat__Intents__0..n` | `["chat"]` | 通告能力意图 |
| `Shutdown__TimeoutSeconds` | `30` | 优雅停机宽限 |

### 5.11 Router 专属

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

### 5.12 其他

| 变量 | 默认 | 作用 |
|---|---|---|
| `OPENAGENT_ALLOW_INSECURE_TLS` | `false` | 部署逃生门：跳过 LLM、S3、MCP、RAG、OIDC 回调等出站请求的 TLS 证书校验。仅自签证书受控场景使用，生产优先安装内部 CA |
| `Secrets__{tenantId}__{name}` | 无 | 租户密钥的配置引用形式（`Backend/src/OpenAgent.Engine/Config/ConfigurationSecretResolver.cs`） |

## 6. Compose 部署配置

应用的共享 `.env` 位于 `deploy/openagent/`；Engine / Router 专用配置在其子目录，Nginx 配置在 `deploy/nginx/`。
不再使用顶层 `.env` 或数据库/S3的 `OPENAGENT_*` 别名，各文件直接填写原生变量。
项目/网络/镜像名称固定，可选项均注释；加载过程见 [部署指南](deployment.md)。

| 位置 | 主要配置 |
|---|---|
| `deploy/openagent/.env` | Production、Redis、JWT/Keycloak、Issuer/discovery、Audience/ClientId；可选 Basic、API Key、CORS、OTLP 与服务版本 |
| `deploy/openagent/engine/.env` | 完整数据库连接串、迁移开关、S3 地址与凭据、文件资产、心跳；可选 bucket/region、分享基地址、Node TLS |
| `deploy/openagent/router/.env` | Engine 内部地址、意图识别超时；可选 Intern-Decision 密钥 |
| `deploy/nginx/.env` | server name、入口端口、请求体上限、证书目录、维护模式目录 |

只有 Nginx 文件由脚本做证书预检时 source；应用文件仅由 Compose 解析，连接串的分号不经过 shell 执行。
含 `$` / `#` 的凭据应将整个值放在单引号内，避免 Compose 插值改变字面量。
`ASPNETCORE_URLS` 默认来自两个 Dockerfile 的 ENV（Engine 5208、Router 5001）；容器服务地址仍需显式指向 `engine` / `router`。
基础设施 Compose 与自身变量保持原样，使用单独的基础设施环境文件。


## 7. 前端连接

Chat 只使用浏览器中保存的连接，不读取前端环境变量或部署配置文件。首次打开页面选择 Router 或直连 Engine，
填写当前模式的一个完整 HTTP(S) 后台地址；支持路径前缀，不允许账号、密码、查询参数或片段。
默认连接模式为 Router，地址为空时直接显示连接页，不请求后台 API，也不猜测 localhost 端口。
地址和连接模式保存在 localStorage，认证凭据保存在当前标签页的 sessionStorage。
租户显示默认值为 development，登录后以服务端认证身份中的租户为准。

旧版 `VITE_OPENAGENT_*` 前端构建变量及 `OPENAGENT_TENANT_ID` 不再读取。
认证参数（authority、clientId、scopes、模式）由目标后台 `GET /api/v1/auth/config` 下发。
独立前端部署见[部署指南](deployment.md#前端连接)。

## 8. 必填与缺失影响总览

| 变量 | 适用场景 | 缺失 / 非法后果 |
|---|---|---|
| `Authentication__Authority` / `__Audience` / `__ClientId` | `Mode=JwtBearer`（可选启用） | Engine 与 Router **启动失败**；生产 json 三者为空，必须经 `.env` 或直接环境变量提供 |
| `Authentication__Mode=Basic` 且非 Development 且 `__BasicPassword` 为空 | — | 启动失败（Production 必须配置密码；用户名含 `:` 或租户为空同样拒绝启动） |
| `RequireHttpsMetadata=false` 且（非 Development 或 Authority 非 localhost） | 本地 HTTP 联调外 | 启动失败 |
| `ConnectionStrings__OpenAgentDatabase` | Engine | 键完全缺失 → 启动异常；保留默认值 → 只连本机库（生产不可用） |
| `FileAssets__ObjectStorage__*` | `FileAssets__Enabled=true` | 桶名/区域为空或凭证不成对 → 启动失败（默认值已满足） |
| `OpenTelemetry__OtlpEndpoint` | 可选 | 不设 → 禁用 OTLP 导出（可运行）；设了但 URI 非法 → 启动异常 |
| `OpenCode__Executable` / `PATH` 中的 opencode | 聊天功能 | 启动正常，**首次聊天报错**（DependencyUnavailable） |
| `ConnectionStrings__Redis` | 可选 | Engine/Router 降级运行（无会话缓存、静态路由、内存缓存） |
| 应用各 `.env` | Compose 部署 | `check` 只校验配置结构；实际连接、凭据与认证选项由服务启动校验，不再经顶层变量转译校验 |

## 9. 相关文档

- [部署指南](deployment.md)：Compose 启动、TLS 证书、数据卷与镜像更新流程
- [Keycloak 本地认证集成](../integrations/keycloak.md)：Realm 导入、用户与租户创建、验证命令
- [认证模块文档](../modules/security.md)：认证方案行为矩阵与源码索引
- [文件资产](../integrations/file-assets.md)、[OpenCode 运行时](../integrations/opencode-runtime.md)、[可观测性](../overview/Observability.md)
