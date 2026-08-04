# Engine — 运行时与 Host 适配

本目录包含 Agent.Engine 的运行时管理和 Host 层文档。

## 子域

| 域 | 说明 | 功能点 |
|----|------|--------|
| [maf/](./maf/) | Microsoft Agent Framework 统一运行时 | maf |
| [host/](./host/) | Host 层（ASP.NET Core 端点、错误处理） | chat-api, error-handling |
| [runtime/](./runtime/) | Engine 运行时管理（配置、健康检查、停机等） | config-management, config-hotreload, health-check, graceful-shutdown, service-registration, capability-registration |
