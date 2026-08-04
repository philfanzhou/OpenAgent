# E2E 测试

## 用途
运行完整的端到端测试流程（构建 + 启动 5 个服务 + 测试 + 清理）。

## 触发条件
- 用户要求"跑 E2E"、"端到端测试"、"完整测试"
- 修改了跨服务的代码需要验证
- 发布前验证

## 输入参数
- `--skip-llm`: 跳过需要真实 LLM API 调用的测试
- `--skip-build`: 跳过构建步骤（服务已构建时）
- `--provider <id>`: LLM 提供商，默认 `xiaomi-mimo`

## 工作流程

### 步骤 1: 检查 API Key 配置
确认 `TestCode/.env` 文件存在且包含所需 API Key：
```powershell
# 查看当前可用提供商
cd TestCode/scripts
Import-Module ./lib/test-helpers.psm1 -Force
Get-AvailableProviders
```
如果 `.env` 不存在，提示用户：`cp TestCode/.env.example TestCode/.env` 并填入真实 Key。

如果使用 `--skip-llm`，可跳过此检查。

### 步骤 2: 执行 E2E 测试脚本
```powershell
cd TestCode/scripts

# 完整测试（含 LLM）
./test-e2e.ps1 -Provider <provider>

# 跳过 LLM 测试
./test-e2e.ps1 -SkipLlmTests

# 服务已启动，只跑测试
./test-e2e.ps1 -SkipBuild -Provider <provider>
```

### 步骤 3: 检查结果
脚本会自动完成构建 → 启动服务（见 `.agent/skills/service-lifecycle.md`）→ 运行集成测试 → 清理端口。

### 步骤 4: 端口冲突恢复
端口被占用时运行 `./cleanup-ports.ps1`（见 `.agent/skills/service-lifecycle.md`），然后重试步骤 2。

### Playground 启动约束

通过 TestChat Playground 驱动 Channels 时，必须同时满足：

1. delivery mode 使用 `expectReplies`，并校验返回的 reply 数量；
2. 设置 `Channels__Outlook__Enabled=false`，避免本地测试启动真实邮箱轮询；
3. 设置 `Channels__Teams__DefaultTenantId=tenant-001`，使 Playground 生成的消息通过租户校验。

缺少其中任一项时，不得把空响应或 Outlook 外部依赖失败判定为 Channels 回归。

## 注意
- 服务端口详见 `.agent/skills/service-lifecycle.md`
- 完整 E2E 约需 5-10 分钟（含 LLM），跳过 LLM 约 2 分钟
- 服务必须按顺序启动（脚本已处理依赖）
- 运行前确保上述 5 个端口未被占用

## 参考文件
- 测试环境详情：`TestCode/README.md`
- **完整 E2E 文档**：`TestCode/docs/e2e-test-guide.md` — 架构、API 端点、Agent 配置 JSON 结构、数据流链路、Redis Key 结构、常见问题排查
- MCP+Skill 测试：`TestCode/docs/mcp-test-guide.md`
- Skill 对比测试：`TestCode/docs/skill-demo-test-guide.md`
- 脚本帮助：`TestCode/scripts/lib/test-helpers.psm1`
- 集成测试项目：`TestCode/Agent.TestEngine/`

## 验证方法
- 5 个服务依次启动成功
- 所有集成测试通过
- 最终端口被清理干净
