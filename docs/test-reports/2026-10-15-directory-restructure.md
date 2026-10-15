# 后端目录与边界重组验证记录

日期：2026-10-15。实施基线：`1ee7473`；分支：`dev`。对应 [R01](../planning/TODO.md)；依据 [目录结构图](../architecture/新架构方案.html)，保留现有运行行为。本文记录本地工作区验收，不表示已提交、已合并或已部署。

## 实施范围

- `OpenAgent.Core` → `OpenAgent.Application`，`OpenAgent.Infrastructure` → `OpenAgent.Framework`。
- 独立 Hosting 并入 `OpenAgent.Engine/Hosting`；Engine 移除对执行层的引用，Router 只使用 Engine 中的 Hosting 类型。
- 文件、RAG、Skill 目录、内置能力、调用授权及 Runner 客户端归 Capabilities；共享目录接口和无 SDK 的能力定义归 Contracts。MAF 执行、MCP/Skill SDK 适配留在 Application。
- 同步项目、namespace、测试归属、架构门禁、CI 路径、开发规则和现行文档。统一注册入口为 `AddAgentApplication`，不保留重复注册或兼容项目。
- 不实现 ACP、Worker、RunGrant、审批或新表，不修改原架构 HTML；Runner 实现保持不变，仅更新涉及旧 Hosting 名称的注释。

## 实测结果

完整 solution 构建通过：**0 警告、0 错误**。完整默认回归：**919 通过、24 跳过、0 失败**。相比迁移前的 917 通过，增加 2 条架构检查；业务测试及环境门控跳过数量不变。

| 测试项目 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| Contracts | 58 | 0 | 0 |
| Application | 286 | 0 | 2 |
| Capabilities | 153 | 0 | 0 |
| Engine（含原 Hosting） | 218 | 0 | 0 |
| Router | 124 | 0 | 0 |
| Framework | 32 | 0 | 0 |
| Architecture | 12 | 0 | 0 |
| Runner | 36 | 0 | 22 |

复现命令：

```sh
dotnet build Backend/OpenAgent.sln --nologo --verbosity minimal
dotnet test Backend/OpenAgent.sln --no-build --nologo --verbosity minimal
```

认证测试另行定向执行 2 项通过：

```sh
dotnet test Backend/tests/OpenAgent.Engine.Tests/OpenAgent.Engine.Tests.csproj --filter FullyQualifiedName~AuthenticationEndpointTests --nologo --verbosity minimal
```

## 差异与边界核对

- 对迁移前 412 个生产 C# 实现文件逐一映射检查，无实现文件遗漏；归一化 namespace、using、契约可见性、注册名称及 BOM 后，未发现其他实现差异。
- 核对旧测试方法名称；原项目名对应的架构检查改为新项目规则，业务测试未删减。
- 项目引用和 solution 路径有效；`git diff --check` 通过。
- 架构测试检查实际 assembly 引用、Application/Capabilities 内部模块边界、Capabilities 不引用 Agent/MCP SDK，以及 Router 不使用 Engine 的非 Hosting 类型。
- EF schema 与迁移 ID 未改变；仅随 Framework 迁移更新 namespace 和模型类型标识。不新增数据库迁移。

## 本次修正的测试适配

合并测试项目后，Engine.Host 的 `appsettings.json` 会进入 Engine.Tests 输出目录。认证 fixture 原本依赖默认配置，因而意外读入 Host 的额外 OIDC scopes；现明确清空来源并先注册内存配置，再设置监听地址。未修改生产认证逻辑或预期 scope。

Engine.Host 经 Application 组合业务能力，没有直接使用 Capabilities 类型；架构测试按实际 metadata 引用断言，不为满足错误断言添加无用引用。

## 未执行项目

- macOS 环境下的 Linux Bubblewrap 与真实 Runner 环境门控测试共 24 项仍跳过，未证明真实沙箱执行链路。
- 未运行远端 CI、容器构建启动、真实数据库迁移或生产部署；前端未修改，本次未重复执行前端验证。
- 独立 reviewer 子进程因缺少模型凭据未产生审查结果，不计入验收证据。本记录依据本地差异核对、构建及测试结果。
