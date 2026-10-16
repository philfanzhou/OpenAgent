# C# ACP SDK 工程与目录设计

> v0.3 · 2026-10-16。唯一 SDK 目录为 `Backend/src/OpenAgent.Runtime.Acp`，只产出一个 SDK 包。下图是完整目标；当前仅传输基线和测试控制台已落地，其他目录随阶段创建，不预建空目录。

## 1. 目录结构

```text
Backend/
├── OpenAgent.sln
├── Directory.Build.props                     # 沿用 net8.0、Nullable 等约束
├── Directory.Packages.props                  # 唯一包版本管理入口
├── src/
│   └── OpenAgent.Runtime.Acp/
│       ├── OpenAgent.Runtime.Acp.csproj       # 唯一可发布 SDK 项目
│       ├── Protocol/
│       │   ├── Generated/                    # 固定 schema 生成的 DTO/方法/context
│       │   ├── JsonRpc/                      # RpcId、envelope、错误
│       │   ├── Serialization/                # 手写 converter 与字段兼容
│       │   └── Validation/                   # wire 及类型校验
│       ├── Transport/                        # IAcpTransport、Stream、NDJSON
│       ├── Rpc/                              # 请求表、独立分派、有序 writer、取消
│       ├── Client/
│       │   ├── Connections/                  # 角色 API、握手和能力门控
│       │   ├── Sessions/                     # Session、PromptTurn、更新路由
│       │   └── Handlers/                     # 权限、文件、终端、elicitation 接口
│       ├── Agent/
│       │   ├── Hosting/                      # Host、注册、Run 生命周期
│       │   ├── Sessions/                     # 活跃会话和轮次
│       │   ├── Handlers/                     # 必需/可选 Agent 接口
│       │   └── Context/                      # 反向 Client 请求和通知
│       ├── Processes/                        # Launcher、租约、stdio、stderr、退出
│       ├── Diagnostics/                      # 脱敏事件、ActivitySource、Meter
│       └── Extensions/                       # 必要 builder / DI 注册，无新包
├── tests/
│   └── OpenAgent.Runtime.Acp.Tests/
│       ├── OpenAgent.Runtime.Acp.Tests.csproj
│       ├── Protocol/ Transport/ Rpc/         # 模型、framing 与 RPC 单元/契约测试
│       ├── Client/ Agent/ Processes/         # 角色、会话和进程测试
│       ├── Console/                          # 启动控制台的端到端场景
│       ├── Architecture/                     # 禁止平台依赖、禁止双实现
│       ├── Interop/ Load/                    # 跨语言、压力与慢消费
│       ├── Fixtures/                         # 固定 wire 与脱敏 transcript
│       └── TestDoubles/                      # 分片流、阻塞流、确定性对端
├── tools/
│   ├── OpenAgent.Runtime.Acp.TestConsole/
│   │   ├── OpenAgent.Runtime.Acp.TestConsole.csproj
│   │   ├── Program.cs                        # 命令入口与退出码
│   │   ├── ConsoleApplication.cs             # 命令解析；随后按需拆 Commands
│   │   ├── Scenarios/                        # 每个新增能力增加对应测试场景
│   │   ├── Agents/                           # 调用 SDK handler 的确定性 Agent
│   │   └── Reporting/                        # 后续结构化结果、脱敏及状态
│   └── OpenAgent.Runtime.Acp.CodeGen/         # 后续生成器，不发布
└── samples/acp-demo/                          # 旧 Demo，迁移后退役

docs/integrations/acp-dotnet-sdk/               # 所有正式文档
└── schema/v1/                                # 后续固定 schema/meta/lock 输入
```

## 2. 三个工程的关系

- SDK：`IsPackable=true`，PackageId / AssemblyName / RootNamespace 前缀为 `OpenAgent.Runtime.Acp`，不引用测试、控制台或其他平台项目。
- 测试：`IsPackable=false`、xUnit，引用 SDK 和控制台工程；验证 SDK 行为与真实控制台进程，复用集中测试依赖。
- 控制台：`OutputType=Exe`、`IsPackable=false`，只引用 SDK；测试场景不得包含另一套 framing/RPC/Client 实现。
- 三者加入现有 `Backend/OpenAgent.sln`，不建立另一套 SDK solution、集中包版本文件或五个 SDK 项目。
- 后续 CodeGen 是构建工具；测试对端和 AOT smoke 是测试资产，都不形成对外 SDK 包。

## 3. 代码组织规则

生成文件不手改，手写兼容代码不放 Generated；核心模型、RPC 机制和角色对象分离。一个文件一个主要类型，内部实现优先 internal；测试访问内部类型前检查 csproj，不为测试扩大公开面。

原目标目录的 Demo 文件夹结构没有兼容承诺，可按本设计调整；当前工作区目标目录为本轮新建。现有 `Backend/samples/acp-demo` 暂保留回归作用，不能作为新 SDK 的项目依赖。

迁移映射：JsonLineTransport → Transport；ClientConnection 的请求循环 → 经探针重建的 Rpc + Client；AcpAgent/AgentSession → Agent 生命周期与控制台 Echo handler；AgentProcess → Processes；DemoClient/Program → 测试控制台命令。迁移不是直接改名，必须同步迁移测试。

正式文档保留在 docs，源码目录不散放 README。新增一个能力时，SDK 实现、对应测试、控制台场景、场景测试和文档必须一起更新。
