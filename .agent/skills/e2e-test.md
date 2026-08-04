# E2E 测试与服务生命周期

运行完整的端到端测试，以及启动/停止测试所需的后台服务。

## 触发条件

- "跑 E2E"、"端到端测试"、"完整测试" → 运行 E2E
- "启动测试环境"、"运行服务" → 启动服务
- "停止服务"、"清理环境" → 停止服务

## 服务清单

| 服务 | 端口 | 说明 |
|------|------|------|
| TestMCP | 8090 | MCP 服务器 |
| TestSkillService | 8091 | 技能服务 |
| TestSSO | 5003 | OAuth2 认证 |
| TestChat | 8080 | 测试聊天与 Playground 页面 |
| Engine | 5208 | Agent 引擎 |

启动顺序必须遵循上表（依赖顺序）；停止顺序相反。

## 相关脚本

| 脚本 | 路径 |
|------|------|
| 启动服务 | `TestCode/scripts/start-services.ps1` |
| 清理端口 | `TestCode/scripts/cleanup-ports.ps1` |
| 检查端口 | `TestCode/scripts/devtools/check-ports.ps1` |
| 检查 Engine | `TestCode/scripts/devtools/check-engine-now.ps1` |

---

## 运行 E2E 测试

### 输入参数

- `--skip-llm`: 跳过需要真实 LLM API 调用的测试
- `--skip-build`: 跳过构建步骤（服务已构建时）
- `--provider <id>`: LLM 提供商，默认 `xiaomi-mimo`

### 步骤

1. 确认 `TestCode/.env` 存在：
   ```powershell
   cd TestCode/scripts
   Import-Module ./lib/test-helpers.psm1 -Force
   Get-AvailableProviders
   ```

2. 执行测试（自动完成构建 → 启动 → 测试 → 清理）：
   ```powershell
   ./test-e2e.ps1 -Provider <provider>
   ./test-e2e.ps1 -SkipLlmTests
   ```

3. 端口冲突时：`./cleanup-ports.ps1` 后重试

### Playground 启动约束

1. delivery mode 使用 `expectReplies`，校验 reply 数量
2. 设置 `Channels__Outlook__Enabled=false`
3. 设置 `Channels__Teams__DefaultTenantId=tenant-001`

---

## 手动启动/停止服务

### 启动

```powershell
cd TestCode/scripts/devtools
./check-ports.ps1            # 确认端口空闲
cd ../
./start-services.ps1         # 按依赖顺序启动
./devtools/check-ports.ps1   # 验证
./devtools/check-engine-now.ps1
```

### 停止

```powershell
cd TestCode/scripts
./cleanup-ports.ps1
./devtools/check-ports.ps1   # 确认 5 个端口已释放
```

### 常见问题

| 问题 | 解决 |
|------|------|
| 端口被占用 | `./cleanup-ports.ps1` |
| 进程杀不掉 | `devtools/kill-port.ps1 <端口>`（管理员权限） |
| Engine 连不上 Redis | 确认 Redis 可达 |
| 认证失败 | 确认 TestSSO 在 5003 端口运行 |
| SQLite 锁残留 | 删除 `TestCode/Data/*.db-shm` 和 `*.db-wal` |

---

## 参考

- 完整 E2E 文档：`TestCode/docs/e2e-test-guide.md`
- MCP+Skill 测试：`TestCode/docs/mcp-test-guide.md`
- Skill 对比测试：`TestCode/docs/skill-demo-test-guide.md`
- 集成测试项目：`TestCode/Agent.TestEngine/`
