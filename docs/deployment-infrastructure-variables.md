# 基础设施和 HTTPS gateway 环境变量

使用外部数据库或 S3 时，无需启动对应内置服务。Basic 不需要 Keycloak；只为选中的服务准备文件。独立应用变量见 [应用变量清单](deployment-variables.md)。

空值均为所选服务的必填凭据，其他项有默认值。证书目录中需提供 `tls.crt` 和 `tls.key`；Keycloak、gateway 启动前必须准备有效证书。

## postgres

模板：[postgres.env.example](../deploy/infrastructure/postgres.env.example)。

| 变量 | 默认值 |
|---|---|
| `OPENAGENT_INFRA_PROJECT` | `openagent-infrastructure` |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` |
| `POSTGRES_PASSWORD` | 空（必填） |
| `POSTGRES_DB` | `openagent` |
| `POSTGRES_USER` | `openagent` |
| `OPENAGENT_POSTGRES_PORT` | `5432` |

## redis

模板：[redis.env.example](../deploy/infrastructure/redis.env.example)。

| 变量 | 默认值 |
|---|---|
| `OPENAGENT_INFRA_PROJECT` | `openagent-infrastructure` |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` |
| `OPENAGENT_REDIS_PORT` | `6379` |

## minio

模板：[minio.env.example](../deploy/infrastructure/minio.env.example)。

| 变量 | 默认值 |
|---|---|
| `OPENAGENT_INFRA_PROJECT` | `openagent-infrastructure` |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` |
| `MINIO_ROOT_USER` | 空（必填） |
| `MINIO_ROOT_PASSWORD` | 空（必填） |
| `S3_BUCKET` | `openagent-files` |
| `OPENAGENT_MINIO_PORT` | `9000` |
| `OPENAGENT_MINIO_CONSOLE_PORT` | `9001` |

## keycloak

模板：[keycloak.env.example](../deploy/infrastructure/keycloak.env.example)。

| 变量 | 默认值 |
|---|---|
| `OPENAGENT_INFRA_PROJECT` | `openagent-infrastructure` |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` |
| `KC_BOOTSTRAP_ADMIN_PASSWORD` | 空（必填） |
| `KC_BOOTSTRAP_ADMIN_USERNAME` | `admin` |
| `KC_HOSTNAME` | `https://localhost:58081` |
| `OPENAGENT_KEYCLOAK_PORT` | `58081` |
| `OPENAGENT_CHAT_PUBLIC_URL` | `http://localhost:8081` |
| `OPENAGENT_TLS_CERT_DIR` | `../openagent/nginx/certs` |

## gateway

模板：[gateway.env.example](../deploy/openagent/gateway.env.example)。

| 变量 | 默认值 |
|---|---|
| `OPENAGENT_COMPOSE_PROJECT` | `openagent-gateway` |
| `OPENAGENT_APP_NETWORK` | `openagent-infrastructure` |
| `OPENAGENT_PUBLIC_HOST` | `localhost` |
| `OPENAGENT_CHAT_PORT` | `8081` |
| `OPENAGENT_ROUTER_PORT` | `8082` |
| `OPENAGENT_ENGINE_PORT` | `8083` |
| `OPENAGENT_TLS_CERT_DIR` | `./nginx/certs` |
| `OPENAGENT_NGINX_CLIENT_MAX_BODY_SIZE` | `1100m` |


## 构建和组合启动

| 变量 | 默认值 | 使用方 |
|---|---|---|
| `OPENAGENT_ENGINE_IMAGE` | `openagent-engine:latest` | 构建、Engine |
| `OPENAGENT_ROUTER_IMAGE` | `openagent-router:latest` | 构建、Router |
| `OPENAGENT_RUNNER_IMAGE` | `openagent-runner:latest` | 构建、Runner |
| `OPENAGENT_DOCKER_MODE` | 构建 `auto`；部署 `docker` | 脚本进程环境；命令行参数优先 |
| `OPENAGENT_IMAGE_TAR_DIR` | 空，不导出 | 构建脚本进程环境；命令行参数优先 |
| `OPENAGENT_COMPOSE_PROJECT` | `openagent-app` | 应用组合入口 |
| `OPENAGENT_ENGINE_ENV_FILE` | `./engine.env` | 应用组合入口 |
| `OPENAGENT_ROUTER_ENV_FILE` | `./router.env` | 应用组合入口 |
| `OPENAGENT_RUNNER_ENV_FILE` | `./runner.env` | 应用组合入口 |
| `OPENAGENT_CHAT_ENV_FILE` | `./chat.env` | 应用组合入口 |
| `OPENAGENT_POSTGRES_ENV_FILE` | `./postgres.env` | 基础设施组合入口 |
| `OPENAGENT_REDIS_ENV_FILE` | `./redis.env` | 基础设施组合入口 |
| `OPENAGENT_MINIO_ENV_FILE` | `./minio.env` | 基础设施组合入口 |

组合入口的文件路径相对各自 Compose 目录解析；文件内部变量互相隔离。独立应用的项目名默认值见应用变量表。脚本只把 `--env-file` 交给 Compose，不执行该文件；脚本进程变量通过调用环境或命令行提供。
