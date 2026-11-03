# Keycloak 本地认证集成

本目录提供 OpenAgent 的本地 Keycloak OIDC 集成。它只用于本地开发和集成测试，生产环境应替换为 HTTPS、正式数据库、密钥管理和企业域控/LDAP 配置。

## 启动

Keycloak 为可选服务；默认 Basic 登录不启动它。需要本地验证 OIDC 时，基础设施命令显式添加 `--profile keycloak`：

```bash
docker compose -p openagent-infrastructure \
  -f deploy/infrastructure/docker-compose.yml \
  --env-file .env \
  --profile storage --profile keycloak up -d

scripts/build-images.sh --env-file .env

scripts/deploy.sh --env-file .env --docker-mode docker
```

访问（证书已被系统信任时）：

- 工作台：`https://localhost:8081`
- Keycloak 管理台：`https://localhost:58081/admin/`
- Keycloak Realm：`openagent`
- 管理员：本地临时默认值为 `admin/admin`；生产环境必须在基础设施 Compose 注释项中设置强管理员凭据。
- 用户和租户组织：不在 Realm 文件中预置，需要在管理台手动创建

公开地址固定使用 HTTPS。将 PEM 证书 `tls.crt`、私钥 `tls.key` 放入与 Nginx 共用的
`deploy/openagent/nginx/certs/` 目录；应用 Nginx 和 Keycloak 共用该目录。
自定义证书位置时按两套 Compose 文件内的挂载注释修改路径。
Keycloak 容器内 HTTPS 端口固定为 `8443`；生产 Router/Engine 使用可信 HTTPS discovery，可留空 MetadataAddress 从公开 issuer 派生。

Realm、SPA Client、API audience、`tenant_id` claim 和 Organization 能力由 [openagent-realm.json](../../../deploy/infrastructure/keycloak/realm/openagent-realm.json) 导入；用户、密码、邮箱和租户组织由业务管理员手动维护。

当前仓库没有独立的 User Profile JSON；Realm 导入文件只负责 Realm、Client、Claim Mapper 和 Organization 能力。用户属性与租户组织由业务管理员按实际 Keycloak 策略维护。

停止应用时只删除应用容器，不影响 PostgreSQL、Redis、MinIO 数据：

```bash
docker compose -p openagent-app \
  -f deploy/openagent/docker-compose.yml \
  down
```

确认不再需要这套隔离的本地基础设施后，才清理基础设施项目和数据卷：

```bash
docker compose -p openagent-infrastructure \
  -f deploy/infrastructure/docker-compose.yml \
  down -v
```

不要对正在运行的其他 Compose 项目执行 `down -v`。基础设施项目由 [deploy/infrastructure/docker-compose.yml](../../../deploy/infrastructure/docker-compose.yml)
提供，应用代码镜像由 [deploy/openagent/docker-compose.yml](../../../deploy/openagent/docker-compose.yml) 提供。

## 功能开关

认证默认是 Basic。需要 OIDC 时，按应用 Compose 中的注释同时修改 Engine 和 Router 的认证配置，并在基础设施 Compose 启用 Keycloak profile。生产环境要求使用可信 HTTPS discovery：

- 默认使用 Basic，Keycloak 默认关闭。Production Basic 必须配置 `OPENAGENT_AUTH_BASIC_PASSWORD`；配置密码后不接受内置开发账号。只有 Development 且未配置密码时才保留 `admin/admin`、`test/test`。
- `Authentication__EnableApiKey` 默认关闭；需要时在 Engine 与 Router 的 Compose 注释中启用。API Key 记录位于 `openagent.third_party_api_keys`。
- Keycloak/OIDC 登录项在 Engine 与 Router 的 Compose 注释中提供；AD 只是 Keycloak 的一种身份源。接入真实 AD 时，需要在 Keycloak 中配置 LDAP/AD User Federation。
- OIDC 的 Authority、MetadataAddress、Audience 和 ClientId 在 Compose 注释中配置；正式部署固定要求 HTTPS 元数据，时钟容差为 60 秒。
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
curl -kfsS https://localhost:8082/api/v1/auth/config
curl -ki https://localhost:8082/api/v1/agent/me
```

第二个请求应为 `401`。在管理台手动创建用户和 Organization 后，浏览器登录应显示 JWT 中配置的用户名、邮箱和租户。

手动创建时，用户属性 `tenant_id`、Organization 属性 `tenant_id` 和 Organization alias 应保持一致；否则 OpenAgent 会因为缺少可信租户 Claim 拒绝 Agent/管理请求。
