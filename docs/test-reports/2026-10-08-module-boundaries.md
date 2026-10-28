# 模块边界验证记录

日期：2026-10-08。实施基线：`9ba17fbf28839696767044dfa8238b85a1690089`；分支：`codex/module-boundaries`。范围：本批 Core/前端模块拆分与依赖检查。当前代码入口见 [开发指南](../overview/DevelopmentGuide.md)，剩余范围见 [后续任务](../planning/TODO.md)。

后端构建通过；最终完整回归 917 项通过、0 失败、24 项环境门控跳过。架构检查另在 Release 配置复验，10 项通过。前端 `pnpm check` 通过：18 个测试文件、153 项测试，类型检查与生产构建成功。

| 后端测试项目 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| Contracts | 58 | 0 | 0 |
| Core | 439 | 0 | 2 |
| Engine | 107 | 0 | 0 |
| Hosting | 111 | 0 | 0 |
| Router | 124 | 0 | 0 |
| Infrastructure | 32 | 0 | 0 |
| Architecture | 10 | 0 | 0 |
| Runner | 36 | 0 | 22 |

复现命令：`dotnet build Backend/OpenAgent.sln --no-restore`；`dotnet test Backend/OpenAgent.sln --no-build --no-restore -m:1`；Release 架构检查用 `dotnet test Backend/tests/OpenAgent.Architecture.Tests/OpenAgent.Architecture.Tests.csproj --configuration Release --no-restore`。前端在 `Frontend/OpenAgent.Chat` 下运行 `pnpm check`。首次检出先恢复依赖。

构建仍报告既有 ImageSharp 3.1.12 的 5 条 NU1902/NU1903 提示；本批没有修改包版本。前端仍有第三方 PURE 注释与 bundle 大小提示。依赖升级和加载优化另立任务，不将它们当作拆分完成条件。

首轮后端并行测试中 Router 的健康探测成功场景失败，该测试配置超时为 500 毫秒。[推断] 可能由并行负载触发超时；原测试使用空日志器，无法确认具体异常。单独复验 2 项通过，最终按项目串行回归 Router 全部 124 项通过。产品超时设置和该测试断言未更改。

macOS 默认跳过 Linux Bubblewrap 与真实 Runner 环境门控测试。这些测试需要在既有 Linux CI/Runner 环境执行；本地结果不证明生产容灾、真实模型调用、MCP 认证或风险审批已经实现。
