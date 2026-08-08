# ADR 0004：集中固定安全依赖版本

- 状态：已接受
- 日期：2026-08-08

## 决策

项目继续使用 .NET 8 兼容的依赖线，并通过中央包管理固定以下版本：

- `Microsoft.Data.Sqlite` 8.0.29；
- `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12；
- `System.Text.Json` 10.0.9。

其中后两项是有意保留的传递依赖版本约束，用于避免恢复到已知存在高危漏洞的旧版本。它们不应在没有重新执行依赖安全审计的情况下移除。

## 验证

依赖更新必须通过解决方案构建、全量测试及以下审计：

```bash
dotnet list Backend/OpenAgent.sln package --vulnerable --include-transitive
```

`System.Text.Json` 10.0.9 同时满足当前 AI SDK 的最低版本要求，并可由 `net8.0` 项目引用。本次不变更目标框架，避免将安全修复与框架迁移混入同一个变更。
