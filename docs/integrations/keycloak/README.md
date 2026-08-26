# Keycloak 本地认证集成

本目录提供 OpenAgent 的本地 Keycloak OIDC 集成。它只用于本地开发和集成测试，生产环境应替换为 HTTPS、正式数据库、密钥管理和企业域控/LDAP 配置。

## 启动

当前工作区已有其他 OpenAgent Compose 项目运行时，请使用独立的基础设施项目、应用项目和网络。基础设施 Compose 同时启动 PostgreSQL、Redis、MinIO 和 Keycloak：

```bash
export OPENAGENT_INFRA_NETWORK=openagent-keycloak-infrastructure

OPENAGENT_KEYCLOAK_PORT=58091 \
OPENAGENT_POSTGRES_PORT=55442 \
OPENAGENT_REDIS_PORT=56389 \
OPENAGENT_MINIO_PORT=59010 \
OPENAGENT_MINIO_CONSOLE_PORT=59011 \
docker compose -p openagent-keycloak-infrastructure \
  -f docker-compose.storage.yml \
  up -d

OPENAGENT_CHAT_PORT=58090 \
OPENAGENT_ROUTER_PORT=55011 \
OPENAGENT_ENGINE_PORT=55218 \
OPENAGENT_KEYCLOAK_PORT=58091 \
OPENAGENT_INFRA_NETWORK=openagent-keycloak-infrastructure \
docker compose -p openagent-keycloak-app \
  -f docker-compose.yml \
  up --build -d
```

访问：

- 工作台：`http://localhost:58090`
- Keycloak 管理台：`http://localhost:58091/admin/`
- Keycloak Realm：`openagent`
- 管理员：由 `OPENAGENT_KEYCLOAK_ADMIN_USERNAME` / `OPENAGENT_KEYCLOAK_ADMIN_PASSWORD` 提供，默认值仅适用于本地临时环境
- 测试用户：`demo / openagent-demo`

Realm、SPA Client、API audience、`tenant_id` claim 和本地测试用户由 [openagent-realm.json](../../../docker/keycloak/realm/openagent-realm.json) 导入。
停止应用时只删除应用容器，不影响 PostgreSQL、Redis、MinIO 数据：

```bash
docker compose -p openagent-keycloak-app \
  -f docker-compose.yml \
  down
```

确认不再需要这套隔离的本地基础设施后，才清理基础设施项目和数据卷：

```bash
docker compose -p openagent-keycloak-infrastructure \
  -f docker-compose.storage.yml \
  down -v
```

不要对正在运行的其他 Compose 项目执行 `down -v`。基础设施项目由 [docker-compose.storage.yml](../../../docker-compose.storage.yml)
提供，应用代码镜像由 [docker-compose.yml](../../../docker-compose.yml) 提供。

## 功能开关

认证开关位于 `Authentication` 配置节，Engine 和 Router 应保持一致：

```json
{
  "Authentication": {
    "EnableKeycloak": true
  }
}
```

- `EnableKeycloak` 控制 Keycloak/OIDC 登录入口。AD 只是 Keycloak 的一种身份源；接入真实 AD 时，需要在 Keycloak 中配置 LDAP/AD User Federation。本地 Realm 没有真实域控，因此只验证开关和登录链路。
- Engine 和 Router 固定解析认证 token 中的租户 claim，并在 Agent/管理接口缺少租户时拒绝请求，不提供关闭租户隔离的配置。
- 本地 Realm 已启用 Keycloak Organization，`demo` 用户属于 `development` 组织；申请 `organization` scope 后，访问令牌会包含 `tenant_id=development` 和 `organization` claim。
- 邮箱 scope 已默认开启，测试用户邮箱为 `demo@openagent.local`。

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

第二个请求应为 `401`。浏览器使用 `demo / openagent-demo` 登录后，工作台的身份检查应显示 `demo` 和 `development`。
