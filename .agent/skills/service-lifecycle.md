# 测试服务生命周期管理

## 用途
启动和停止 E2E 测试所需的全部后台服务。

## 服务清单

| 服务 | 端口 | 说明 |
|------|------|------|
| TestMCP | 8090 | MCP 服务器 |
| TestSkillService | 8091 | 技能服务 |
| TestSSO | 5003 | OAuth2 认证 |
| TestChat | 8080 | 测试聊天与 Playground 页面 |
| Engine | 5208 | Agent 引擎 |

启动顺序必须遵循上表从上到下（依赖顺序）；停止顺序相反。

## 相关脚本

| 脚本 | 路径 |
|------|------|
| 启动服务 | `TestCode/scripts/start-services.ps1` |
| 清理端口 | `TestCode/scripts/cleanup-ports.ps1` |
| 检查端口 | `TestCode/scripts/devtools/check-ports.ps1` |
| 检查 Engine | `TestCode/scripts/devtools/check-engine-now.ps1` |

---

## 启动服务

### 触发条件
- 用户要求"启动测试环境"、"运行服务"、"搭测试环境"
- 准备进行集成测试或本地调试

### 输入参数
- `--no-build`: 跳过构建步骤

### 步骤 1: 检查 .env
确认 `TestCode/.env` 存在，不存在则提示：`cp TestCode/.env.example TestCode/.env`

### 步骤 2: 检查端口
```powershell
cd TestCode/scripts/devtools
./check-ports.ps1
```
如有端口被占用，先执行 `../cleanup-ports.ps1`

### 步骤 3: 启动服务
```powershell
cd TestCode/scripts
./start-services.ps1
```

### 步骤 4: 验证
```powershell
cd TestCode/scripts/devtools
./check-ports.ps1         # 确认所有端口已监听
./check-engine-now.ps1    # 确认 Engine 响应正常
```

### 常见问题

| 问题 | 解决 |
|------|------|
| 端口被占用 | `./cleanup-ports.ps1` |
| Engine 连不上 Redis | 确认 Redis (<redis-host>:<redis-port>) 可达 |
| 认证失败 | 确认 TestSSO 在 5003 端口运行 |
| 启动很慢 | 首次启动需要构建，耐心等待 |

---

## 停止服务

### 触发条件
- 用户要求"停止服务"、"清理环境"、"释放端口"
- 测试完成需要清理

### 步骤 1: 停止所有服务
```powershell
cd TestCode/scripts
./cleanup-ports.ps1
```

### 步骤 2: 验证端口已释放
```powershell
cd TestCode/scripts/devtools
./check-ports.ps1
```
确认上表 5 个端口不再被占用（8090/8091/5003/8080/5208）。

### 常见问题

| 问题 | 解决 |
|------|------|
| 进程杀不掉 | 用管理员权限运行 `devtools/kill-port.ps1 <端口号>` |
| 不确定要不要停 | 先 `check-ports.ps1` 看看哪些服务在跑 |
| SQLite 锁文件残留 | 手动删除 `TestCode/Data/*.db-shm` 和 `*.db-wal` |

---

## 参考文件
- 测试环境架构：`TestCode/README.md`
- 帮助函数：`TestCode/scripts/lib/test-helpers.psm1`

## 验证方法
- 启动后：`check-ports.ps1` 显示 8090/8091/5003/8080/5208 五个端口均在监听，`check-engine-now.ps1` 返回正常响应
- 停止后：`check-ports.ps1` 显示 5 个端口均为空闲，重新启动服务不报端口冲突
