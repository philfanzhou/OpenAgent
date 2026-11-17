# Keycloak 本地认证集成

本目录提供 OpenAgent 的本地 Keycloak OIDC 集成。它只用于本地开发和集成测试，生产环境应替换为 HTTPS、正式数据库、密钥管理和企业域控/LDAP 配置。

## 启动

先按 [部署指南](../operations/deployment.md) 运行 `scripts/deploy.sh init`。
应用项目固定为 `openagent-app`、入口为 `openagent-nginx`；应用通过固定的 `openagent-infrastructure` 网络连接基础设施。
在共享 `deploy/openagent/.env` 中检查公开 Authority 和内部 HTTPS MetadataAddress，须与现有 IdP 一致且 CA 受信任。

```bash
# 基础设施仍单独管理；需要内置 Keycloak 时启用其 profile。
docker compose --env-file deploy/.env -f deploy/infrastructure/docker-compose.yml --profile storage --profile keycloak up -d
scripts/build-images.sh
scripts/deploy.sh
```

访问（证书已被系统信任时）：

- 工作台：`https://localhost:8081`
- Keycloak 管理台：`https://localhost:58081/admin/`
- Keycloak Realm：`openagent`
- 管理员：默认值为 `admin/admin`，仅适用于本地临时环境；生产环境请覆盖 `OPENAGENT_KEYCLOAK_ADMIN_USERNAME` / `OPENAGENT_KEYCLOAK_ADMIN_PASSWORD`
- 用户和租户组织：不在 Realm 文件中预置，需要在管理台手动创建

公开地址固定使用 HTTPS。将 PEM 证书 `tls.crt`、私钥 `tls.key` 放入与 Nginx 共用的
`deploy/openagent/nginx/certs/` 目录；应用 Nginx 和 Keycloak 共用该目录。
自定义位置时使用绝对路径 `OPENAGENT_TLS_CERT_DIR`，两套 Compose 传入同一环境文件。
Keycloak 容器内 HTTPS 端口固定为 `8443`，Router/Engine 仍通过容器网络内 HTTP discovery 访问 Keycloak。

Realm、SPA Client、API audience、`tenant_id` claim 和 Organization 能力由 [openagent-realm.json](../../deploy/infrastructure/keycloak/realm/openagent-realm.json) 导入；用户、密码、邮箱和租户组织由业务管理员手动维护。

当前仓库没有独立的 User Profile JSON；Realm 导入文件只负责 Realm、Client、Claim Mapper 和 Organization 能力。用户属性与租户组织由业务管理员按实际 Keycloak 策略维护。

停止应用时只删除应用容器，不影响 PostgreSQL、Redis、MinIO 数据：

```bash
docker compose -p openagent-app \
  -f deploy/openagent/docker-compose.yml \
  down
```

基础设施生命周期单独管理；停止业务不应删除 PostgreSQL 或 Data Protection 数据卷。

## 功能开关

认证配置统一放在共享 `deploy/openagent/.env`，以 env_file 同时注入 Engine / Router。生产环境示例：

```dotenv
ASPNETCORE_ENVIRONMENT=Production
Authentication__Mode=JwtBearer
Authentication__EnableKeycloak=true
Authentication__AllowDevelopmentAnonymous=false
Authentication__Audience=openagent-api
Authentication__ClientId=openagent-chat
Authentication__Authority=https://localhost:58081/realms/openagent
Authentication__MetadataAddress=https://openagent-infrastructure-keycloak-1:8443/realms/openagent/.well-known/openid-configuration
```

- `Authentication__Mode` 支持 `Basic`（部署默认，任意环境可用）和 `JwtBearer`。Basic 登录密码由必填项 `OPENAGENT_AUTH_BASIC_PASSWORD` 提供（配置后内置开发账号即失效）；切换 Keycloak 时设置 `Authentication__Mode=JwtBearer`、`Authentication__EnableKeycloak=true`，并在 `.env` 中保留该必填密码变量（Compose 启动前校验）。
- `Authentication__EnableApiKey=true` 时，Router 和 Engine 增加数据库中的第三方 Bearer API Key 认证，不访问 Keycloak；该开关与 `Authentication__Mode` 并存，不替换 Basic/JWT Bearer 方案。API Key 记录位于 `openagent.third_party_api_keys`。
- `Authentication__EnableKeycloak` 控制 Keycloak/OIDC 登录入口。AD 只是 Keycloak 的一种身份源；接入真实 AD 时，需要在 Keycloak 中配置 LDAP/AD User Federation。本地 Realm 没有真实域控，因此只验证开关和登录链路。
- `Authentication__Audience` 与 `Authentication__ClientId` 控制 JWT audience 与 OIDC client；生产元数据固定要求 HTTPS、时钟容差固定 60 秒。
- `OPENAGENT_KEYCLOAK_PUBLIC_URL` 是浏览器使用的公开 Issuer 地址；`Authentication__MetadataAddress` 是 Engine/Router 访问 discovery 的服务端地址。
- Engine 和 Router 固定解析认证 token 中的租户 claim，并在 Agent/管理接口缺少租户时拒绝请求，不提供关闭租户隔离的配置。
- 本地 Realm 已启用 Keycloak Organization。创建用户时，需要在用户属性中设置 `tenant_id`，并将用户加入同名 Organization；申请 `organization` scope 后，访问令牌会包含对应的 `tenant_id` 和 `organization` claim。
- 邮箱由 Keycloak 用户资料维护，并通过 OIDC `email` scope 返回。

退出登录会调用 Keycloak 的 OIDC end-session endpoint，并在下一次登录时发送 `prompt=login`，避免浏览器保留 Keycloak SSO 会话导致“退出后直接进入”。

## 认证边界

- Keycloak 负责用户、密码、OIDC 登录和 JWT 签发。
- Router 和 Engine 都验证 issuer、audience、签名和有效期。
- `sub` 是用户身份，`tenant_id` 是本地测试租户 claim；生产环境不能由客户端 header 建立租户身份。
- `MetadataAddress` 只用于容器内访问 Keycloak 的 backchannel discovery；浏览器仍使用 `Authority` 的公开地址。
- OpenAgent 的 Agent、会话、文件、MCP、Skill 和实际副作用授权仍由服务端授权层负责，不由本地 Realm 角色直接替代。

## 验证

```bash
curl -fsS http://localhost:55011/api/v1/auth/config
curl -i http://localhost:55011/api/v1/agent/me
```

第二个请求应为 `401`。在管理台手动创建用户和 Organization 后，浏览器登录应显示 JWT 中配置的用户名、邮箱和租户。

手动创建时，用户属性 `tenant_id`、Organization 属性 `tenant_id` 和 Organization alias 应保持一致；否则 OpenAgent 会因为缺少可信租户 Claim 拒绝 Agent/管理请求。
