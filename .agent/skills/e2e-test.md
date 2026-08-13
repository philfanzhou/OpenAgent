# E2E 测试与服务生命周期

运行完整的端到端测试，以及启动/停止测试所需的后台服务。

## 触发条件

- "跑 E2E"、"端到端测试"、"完整测试" → 运行 E2E
- "启动测试环境"、"运行服务" → 启动服务
- "停止服务"、"清理环境" → 停止服务

## 服务清单

| 服务 | 端口 | 说明 |
|------|------|------|
| chat | 8080 | 聊天与 Playground 页面 |
| engine | 5208 | Agent 引擎 |
| router | 5001 | 网关路由 |
| postgres | 5432 | PostgreSQL 会话存储 |
| redis | 6379 | Redis 注册表/缓存 |
| minio | 9000/9001 | 对象存储（API/Console） |

启动顺序由 `docker compose` 管理依赖；停止顺序相反。

## 相关命令

| 操作 | 命令 |
|------|------|
| 启动服务 | `docker compose up -d` |
| 停止服务 | `docker compose down` |
| 查看服务状态 | `docker compose ps` |
| 查看日志 | `docker compose logs -f <service>` |

---

## 运行 E2E 测试

### 输入参数

- `--skip-llm`: 跳过需要真实 LLM API 调用的测试
- `--skip-build`: 跳过构建步骤（服务已构建时）
- `--provider <id>`: LLM 提供商，默认 `xiaomi-mimo`

### 步骤

1. 启动服务并确认健康：
   ```powershell
   docker compose up -d
   docker compose ps
   curl http://localhost:5208/health
   ```

2. 配置 LLM Provider（在工作台设置中创建，参考根 `README.md`），然后通过聊天界面或 `curl` 调用 engine 端点验证端到端流程。

3. 端口冲突时：`docker compose down` 后重试

### Playground 启动约束

1. delivery mode 使用 `expectReplies`，校验 reply 数量
2. 设置 `Channels__Outlook__Enabled=false`
3. 设置 `Channels__Teams__DefaultTenantId=tenant-001`

---

## 手动启动/停止服务

### 启动

```powershell
docker compose up -d           # 按依赖顺序启动
docker compose ps              # 验证服务状态
curl http://localhost:5208/health  # 验证 Engine 健康
```

### 停止

```powershell
docker compose down            # 停止并清理容器
docker compose ps              # 确认服务已停止
```

### 常见问题

| 问题 | 解决 |
|------|------|
| 端口被占用 | `docker compose down` 后重试 |
| Engine 连不上 Redis | `docker compose restart redis`，确认 Redis 容器健康 |
| Engine 连不上 PostgreSQL | `docker compose restart postgres`，检查 `appsettings.json` 连接串 |

---

## 参考

- 排查手册：`docs/trace-troubleshoot.md`
- 模块文档：`docs/modules/`
- 集成测试项目：`Backend/tests/OpenAgent.Infrastructure.Tests/`、`Backend/tests/OpenAgent.Engine.Tests/`
