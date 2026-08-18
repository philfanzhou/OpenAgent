# PR43 MCP 与 Skill 本地执行安全调研

**调研日期：** 2026-08-17
**适用版本：** Microsoft Agent Framework 1.14.0、ModelContextProtocol.Core 1.4.1

## 结论

OpenAgent 不需要复制 MCP 协议实现或 Agent Skills 工具实现：MCP 客户端、传输与协议协商交给官方 MCP C# SDK；Skill 发现、渐进披露和工具定义交给 MAF 官方 `AgentSkillsProvider`。平台只实现配置目录、Agent 绑定、授权、生命周期和执行策略。

官方 SDK 不提供不可信本地代码的系统级隔离。MAF 的脚本 runner 是扩展点，不是沙盒；MCP stdio transport 会直接在客户端宿主启动进程，也不是沙盒。因此本次采用两条明确分离的边界：

- Skill 脚本：逐 Skill 管理员授权后，发送到独立容器，经 Unix Domain Socket 执行；
- MCP stdio：默认关闭；开启时只作为可信宿主进程能力，受命令、环境变量和工作目录白名单约束。

## 官方能力与平台职责

| 场景 | 官方 SDK 已提供 | OpenAgent 保留职责 |
|---|---|---|
| MCP | initialize、版本协商、Streamable HTTP、legacy SSE、stdio、工具发现/调用、资源类型 | MCP 独立目录、Agent ID 绑定、ACL、配置脱敏、stdio 策略、请求级释放 |
| Skill | `load_skill`、`read_skill_resource`、`run_skill_script`、文件 Skill 发现、路径与符号链接检查 | 包上传与完整性、目录绑定、ACL、脚本逐 Skill 授权、隔离 runner、限额与审计 |

参考：

- [MAF Agent Skills 官方文档](https://learn.microsoft.com/en-us/agent-framework/agents/skills)
- [MAF Agent Skills 设计决策](https://github.com/microsoft/agent-framework/blob/main/docs/decisions/0021-agent-skills-design.md)
- [MCP C# SDK transport 文档](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md)

## MCP 传输与协议版本

### 传输

| 配置类型 | 官方 transport | 使用建议 |
|---|---|---|
| `Http` | `HttpClientTransport` / Streamable HTTP | 新服务默认选择；生产优先使用 HTTPS、服务身份和网络策略 |
| `Sse` | `HttpClientTransport` / SSE mode | 只用于兼容 legacy MCP Server |
| `Stdio` | `StdioClientTransport` | 仅用于受信 Server；进程权限等同 Engine 宿主，不视为隔离执行 |

stdio 策略采用拒绝优先：生产默认 `AllowStdio=false`；开启后，裸命令必须精确匹配白名单，带路径命令必须精确匹配已允许的绝对路径，不能通过相同 basename 绕过；自定义环境变量与工作目录分别使用白名单；不继承宿主全部环境变量。

### 协议版本语义

SDK 在 initialize 阶段协商版本。OpenAgent 的“固定版本”字段表示客户端可接受的最低版本/兼容下限，而不是强制双方精确使用某个版本；未填写时自动协商。因此 UI 使用“自动协商或最低版本”，避免把它描述成精确锁定。

真实官方 SDK fixture 验证：

| 客户端策略 | 最终协商版本 |
|---|---|
| 自动 | `2025-11-25` |
| 最低 `2025-06-18` | `2025-06-18` |
| 最低 `2026-07-28` | `2026-07-28` |

SDK 的版本与 stateless 行为参考 [官方 stateless 文档](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/stateless/stateless.md) 和 [官方发布记录](https://github.com/modelcontextprotocol/csharp-sdk/releases)。

## Skill 脚本安全模型

### 执行链路

```text
Skill 配置页逐项授权
        │
        ▼
MAF AgentSkillsProvider / run_skill_script
        │  runner 再校验 Skill、路径、扩展名、大小、参数
        ▼
Engine ── Unix Domain Socket ── SkillSandbox.Host
                                  │
                                  ▼
                       低权限 uid + python3 -I -B -s
```

当前控制：

- 新上传 Skill 即使包含脚本，`AllowScriptExecution` 默认仍为 `false`；
- ZIP 限 128 个文件、解压后合计 4 MiB，拒绝路径穿越、重复路径和不唯一 `SKILL.md`；
- runner 重新校验所属 Skill、`scripts/` 路径、`.py`、脚本大小、参数数量和长度；
- 沙盒容器无网络、只读根文件系统、`no-new-privileges`、drop all capabilities，只为 root supervisor 保留 `SETUID/SETGID`，脚本降权到 uid 10001；
- `/tmp` 使用 32 MiB tmpfs，带 `noexec,nosuid,nodev`；限制 128 MiB 内存、0.5 CPU、32 PID；
- Python 使用 isolated mode，不读取用户 site 或环境注入；执行有 10 秒超时、进程树终止、stdout+stderr 合计 64 KiB；
- Engine 与沙盒只通过只读挂载的 Unix Socket 通信，沙盒不挂载对象存储、数据库、Redis 或宿主工作区。

### MAF 审批边界

MAF 1.14 默认可为 `load_skill`、资源读取和脚本运行生成 approval request。当前 Chat/SSE 合约没有用户审批回合，保留默认值会让工具停在“需要审批”而无法继续。本实现关闭 MAF 的逐调用审批工具，实际审批边界改为已有管理权限控制的逐 Skill 开关加隔离沙盒。

这不是最终的人机审批体验。若后续 Chat 增加 approval request/response 事件，应恢复 MAF 逐调用审批，使“管理员允许这个 Skill 具备脚本能力”和“用户同意本次执行”形成两级授权。

## 是否需要第三方沙盒

本地开发和单租户受控部署可使用当前 rootless/最小权限容器基线，但普通 OCI 容器共享宿主内核，不应被宣传为对恶意多租户代码的强隔离。

| 风险级别 | 建议 |
|---|---|
| 可信内部 Skill、单租户 | 当前独立容器、无网络、只读根、资源限额可作为上线基线 |
| 不可信 Skill、多租户 | 使用 gVisor 或 Kata Containers 等更强运行时隔离，每次执行创建短生命周期 sandbox |
| Kubernetes Agent 平台 | 通过 RuntimeClass 选择 gVisor/Kata；评估 Agent Sandbox API 的池化、网络与身份隔离 |
| 远程沙盒服务 | 当前 `remote-http` 适配必须置于 mTLS/服务网格或认证代理后；不可把未认证执行端点暴露到普通网络 |

参考：

- [Docker rootless mode](https://docs.docker.com/engine/security/rootless/)
- [Docker seccomp](https://docs.docker.com/engine/security/seccomp/)
- [gVisor 文档](https://gvisor.dev/docs/)
- [Kubernetes RuntimeClass](https://kubernetes.io/docs/concepts/containers/runtime-class/)
- [Kubernetes 多租户隔离](https://kubernetes.io/docs/concepts/security/multi-tenancy/)
- [Kubernetes Agent Sandbox](https://kubernetes.io/blog/2026/03/20/running-agents-on-kubernetes-with-agent-sandbox/)

## 未覆盖与后续建议

- 尚未实现逐次用户审批、租户级并发队列、沙盒实例池和执行身份凭证；
- 仅开放 Python，未开放 shell、PowerShell、JavaScript、C# 脚本；
- MCP stdio 仍在 Engine 宿主执行，配置者必须被视为能部署受信代码的管理员；
- 未在本轮使用外部云 LLM 凭据；工具循环由确定性 OpenAI-compatible fixture 驱动，MCP Server 和 Skill 脚本本身均为真实官方链路；
- 多租户正式开放脚本前，应完成 gVisor/Kata 对比、镜像签名/SBOM、每次执行实例化和外联 allowlist。
