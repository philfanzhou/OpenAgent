# 集成问题诊断

OpenAgent 平台集成问题的排查流程。由外到内逐层检查。

---

## 1. 服务连通性

### 检查所有服务状态
```powershell
cd TestCode/scripts/devtools
./check-ports.ps1
```

预期 5 个端口均在监听：8090、8091、5003、8080、5208。

### 个别服务检查
```powershell
# Engine 健康检查
./check-engine-now.ps1

# MCP 服务检查
curl http://localhost:8090/health

# Skill 服务检查
curl http://localhost:8091/health
```

### 端口被占用
```powershell
cd TestCode/scripts
./cleanup-ports.ps1
```
如果进程杀不掉，用管理员权限：`devtools/kill-port.ps1 <端口号>`

---

## 2. LLM 提供商问题

### 症状
- Agent 无响应或返回空
- 日志显示 API 调用失败
- E2E 测试中 LLM 相关用例失败

### 排查
1. **检查 API Key**：
   ```powershell
   cd TestCode/scripts
   Import-Module ./lib/test-helpers.psm1 -Force
   Get-AvailableProviders
   ```
   确认目标提供商有可用 Key。

2. **检查 Key 格式**：`TestCode/.env` 中 Key 变量名必须是 `{PROVIDER_ID}_API_KEY`（全大写，连字符换下划线），如 `DEEPSEEK_API_KEY`、`XIAOMI_MIMO_API_KEY`。

3. **检查提供商配置**：通过 RedisTool 检查 `llm:registry:<provider>` 中的 Endpoint 是否正确。

4. **测试 API 连通性**：用 curl 直接测试提供商 API 是否可达。

### 常见错误
| 错误 | 原因 | 解决 |
|------|------|------|
| 401 Unauthorized | API Key 错误或过期 | 检查 .env 中的 Key |
| 404 Not Found | Endpoint URL 错误 | 检查 llm/*.json 中的 Endpoint |
| 连接超时 | 网络不通或防火墙 | 检查网络、代理设置 |
| Model not found | ModelId 不存在 | 检查提供商支持的模型列表 |

---

## 3. MCP 工具问题

### 症状
- Agent 调用 MCP 工具失败
- 工具发现列表为空
- SQLite 查询报错

### 排查
1. **检查 MCP 服务**：`curl http://localhost:8090/health`
2. **检查数据库文件**：`TestCode/Data/` 下是否存在 `hrs.db`、`finance.db`、`it.db`
3. **检查数据目录**：环境变量 `MCP_TOOL_DATA_DIR` 或 `OPENAGENT_TEST_DATA_DIR` 是否指向正确的 `TestCode/Data/`
4. **SQLite 锁文件**：如果查询挂起，删除 `*.db-shm` 和 `*.db-wal` 文件

### 参考
- MCP 测试脚本：`TestCode/scripts/integration/test-it-mcp-protocol.ps1`
- MCP 添加指南：`.agent/skills/add-mcp-tool.md`
- MCP 协议测试脚本：`TestCode/scripts/integration/test-it-mcp-protocol.ps1`

---

## 4. Skill 问题

### 症状
- Skill 调用失败或超时
- 业务 Skill 返回的数据不正确

### 排查
1. **检查 Skill 服务**：`curl http://localhost:8091/health`
2. **检查 Skill 列表**：`curl http://localhost:8091/skills/list`
3. **检查 Skill 配置**：通过 RedisTool 检查 `skill:registry:<name>` 中是否存在对应 Skill 定义
4. **注意**：Skill JSON 可能在测试运行时被 `Update-AgentConfigs` 重写

### 参考
- Skill 添加指南：`.agent/skills/add-agent-skill.md`
- Skill-MCP 集成测试：`TestCode/scripts/integration/test-it-skill-mcp-integration.ps1`

> 本提示词负责本地服务、配置与协议联调；已有真实请求的 logs/trace/metrics 排查改用
> `.agent/skills/trace-troubleshoot.md`。

---

## 5. Engine 问题

### 症状
- Engine 启动失败
- Agent 端点无响应
- 日志报错

### 排查
1. **检查启动顺序**：TestMCP → TestSkillService → TestSSO → Engine（必须按此顺序）
2. **检查 Redis 连接**：确认 Redis 可达，Engine 的 `appsettings.json` 中 Redis 连接字符串正确
3. **检查 SSO 连接**：确认 TestSSO 在 5003 端口运行
4. **开启详细日志**：
   ```powershell
   cd TestCode/scripts/devtools
   ./restart-engine-logged.ps1
   ```
5. **检查 Agent 配置**：`Update-AgentConfigs` 是否已将 Agent JSON 更新为当前提供商/框架

### 参考
- Engine 配置：`Backend/OpenAgent/Agent.Engine/src/Host/appsettings.json`
- Engine 诊断：`TestCode/scripts/devtools/check-engine-now.ps1`

---

## 6. 通用修复操作

| 问题 | 操作 |
|------|------|
| 不确定问题在哪 | 从步骤 1 开始逐层排查 |
| 端口冲突 | `cleanup-ports.ps1` |
| 配置漂移 | 通过 RedisTool 重新写入配置数据 |
| 构建过期 | `dotnet clean` 后重建 |
| SQLite 锁 | 删除 `*.db-shm` 和 `*.db-wal` |
| 无法定位根因 | `restart-engine-logged.ps1` 查看详细日志 |

## 参考文件
- 端口检查：`TestCode/scripts/devtools/check-ports.ps1`
- 端口清理：`TestCode/scripts/cleanup-ports.ps1`
- Engine 日志：`TestCode/scripts/devtools/restart-engine-logged.ps1`
