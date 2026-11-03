# Keycloak 可选认证集成

默认认证为 Basic，Keycloak 不参与默认启动。应用变量与必填项见 [部署变量](../../deployment-variables.md)，可选容器参数见 [基础设施变量](../../deployment-infrastructure-variables.md)。本地 Realm 用于联调；生产应配置正式数据库、证书、密钥管理与企业身份源。

## 独立启动

复制 [keycloak.env.example](../../../deploy/infrastructure/keycloak.env.example) 至受保护的配置文件，填写 `KC_BOOTSTRAP_ADMIN_PASSWORD`，按实际入口配置 `KC_HOSTNAME` 和 `OPENAGENT_CHAT_PUBLIC_URL`。默认管理员名 `admin`，没有默认管理员密码。

```bash
scripts/deploy.sh --app keycloak --env-file /absolute/path/keycloak.env
```

证书目录中必须提供 `tls.crt` 和 `tls.key`；默认复用 `deploy/openagent/nginx/certs/`，覆盖位置时推荐绝对路径 `OPENAGENT_TLS_CERT_DIR`。Keycloak 内部 HTTPS 端口固定 8443，对外端口默认 58081。浏览器和 Router/Engine 均需信任签发 CA。

已有其他部署时使用独立的 `OPENAGENT_INFRA_PROJECT`、`OPENAGENT_APP_NETWORK` 和宿主机端口。Engine、Router 若通过 Docker 别名访问 Keycloak，应加入相同网络；跨机器则配置可达的 HTTPS 地址。

只停止此服务可使用其独立 Compose 文件和原配置执行 `stop keycloak`。迁移保留原项目名及 `keycloak-data` 卷，不重建已有 Realm，不对其他服务执行删除数据卷操作。

## Engine 和 Router 配置

在两端各自的环境文件中配置相同的认证信息，例如：

```dotenv
ASPNETCORE_ENVIRONMENT=Production
Authentication__Mode=JwtBearer
Authentication__EnableKeycloak=true
Authentication__AllowDevelopmentAnonymous=false
Authentication__Authority=https://identity.example.com/realms/openagent
Authentication__Audience=openagent-api
Authentication__ClientId=openagent-chat
Authentication__RequireHttpsMetadata=true
Authentication__MetadataAddress=
```

`MetadataAddress` 为空时从 Authority 派生；可单独配置服务器可达的 discovery URL，但生产必须使用可信 HTTPS，不能用 HTTP 容器地址绕过校验。Authority 始终匹配令牌 issuer 和浏览器公开地址。

只使用 Basic 时，无需以上 OIDC 参数；配置 `BasicPassword` 即可在生产使用 Basic。配置密码后禁用 `admin/admin`、`test/test` 开发账号；只有 Development 且未配置密码时保留这些开发账号。

`Authentication__EnableApiKey=true` 可附加数据库中的第三方 Bearer API Key 认证，不访问 Keycloak，也不替换原有 Basic/JWT 方案。具体身份和权限存于 `openagent.third_party_api_keys`。

## Realm、用户与租户

[openagent-realm.json](../../../deploy/infrastructure/keycloak/realm/openagent-realm.json) 包含 Realm、SPA Client、API audience、`tenant_id` Mapper 和 Organization 能力。用户、密码、邮箱、租户组织由业务管理员创建；仓库不预置用户，也没有独立的 User Profile JSON。

- `sub` 是用户身份；Engine、Router 验证 issuer、audience、签名和有效期。
- 用户属性 `tenant_id`、Organization 属性 `tenant_id` 和 Organization alias 应保持一致，并将用户加入组织；申请 `organization` scope 后令牌包含相应 claim。
- 邮箱来自用户资料，通过 `email` scope 返回。
- 生产请求不能通过客户端 header 建立或覆盖租户身份，缺少可信租户 claim 时拒绝 Agent/管理请求；不存在关闭租户隔离的开关。
- AD 是可选身份源，需在 Keycloak 配置 LDAP/AD User Federation；本地 Realm 不包含真实域控。
- Agent、会话、文件、MCP、Skill 及副作用权限由 OpenAgent 服务端授权层处理，不由 Realm 角色直接替代。

前端 OIDC 通过 PKCE 登录；退出调用 end-session endpoint，后续登录发送 `prompt=login`。前端由独立仓库维护，其行为须在真实登录联调中验证。

## 验证

```bash
curl -fsS http://localhost:5001/api/v1/auth/config
curl -i http://localhost:5001/api/v1/agent/me
```

选择 JwtBearer 后，未携带凭据的第二个请求应为 401。浏览器成功登录后验证用户名、邮箱、租户以及退出和重新登录。此处为验证步骤，不代表已经完成真实 IdP 联测。
