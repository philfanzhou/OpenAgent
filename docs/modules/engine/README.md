# Engine — 运行时与 Host 适配

本目录包含 OpenAgent.Engine 的配置、注册、运行时管理与共享 Hosting，以及 OpenAgent.Engine.Host 的 HTTP 宿主文档。

## 子域

| 域 | 说明 | 功能点 |
|----|------|--------|
| [maf/](./maf/) | Microsoft Agent Framework 统一运行时 | maf |
| [host/](./host/) | Host 层（ASP.NET Core 端点、错误处理） | chat-api, error-handling |
| [runtime/](./runtime/) | Engine 运行时管理（配置、健康检查、停机等） | config-management, config-hot-reload, health-check, graceful-shutdown, service-registration, capability-registration |
