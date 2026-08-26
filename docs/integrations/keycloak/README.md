# Keycloak 本地认证集成

本目录提供 OpenAgent 的本地 Keycloak OIDC 集成。它只用于本地开发和集成测试，生产环境应替换为 HTTPS、正式数据库、密钥管理和企业域控/LDAP 配置。

## 启动

当前工作区已有其他 OpenAgent Compose 项目运行时，请使用独立项目名和端口：

```bash
OPENAGENT_KEYCLOAK_PORT=58091 \
OPENAGENT_CHAT_PORT=58090 \
OPENAGENT_ROUTER_PORT=55011 \
OPENAGENT_ENGINE_PORT=55218 \
OPENAGENT_POSTGRES_PORT=55442 \
OPENAGENT_REDIS_PORT=56389 \
OPENAGENT_MINIO_PORT=59010 \
OPENAGENT_MINIO_CONSOLE_PORT=59011 \
docker compose -p openagent-keycloak \
  -f docker-compose.yml \
  -f docker-compose.keycloak.yml \
  up --build -d
```

访问：

- 工作台：`http://localhost:58090`
- Keycloak 管理台：`http://localhost:58091/admin/`
- Keycloak Realm：`openagent`
- 管理员：由 `OPENAGENT_KEYCLOAK_ADMIN_USERNAME` / `OPENAGENT_KEYCLOAK_ADMIN_PASSWORD` 提供，默认值仅适用于本地临时环境
- 测试用户：`demo / openagent-demo`

Realm、SPA Client、API audience、`tenant_id` claim 和本地测试用户由 [openagent-realm.json](../../../docker/keycloak/realm/openagent-realm.json) 导入。清理时只删除本 Compose 项目：

```bash
docker compose -p openagent-keycloak \
  -f docker-compose.yml \
  -f docker-compose.keycloak.yml \
  down -v
```

不要对正在运行的其他 Compose 项目执行 `down -v`。

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
