# ADR 0001：日志框架与存储后端

- 日期：2026-06-25
- 状态：已被当前可观测性设计取代
- 当前事实入口：[`../overview/Observability.md`](../overview/Observability.md)

## 历史决策

本 ADR 曾决定：应用使用 `Microsoft.Extensions.Logging` 与 Serilog，并由应用同时输出 Console、直推 Loki，使用 Grafana 查询。

其中 `ILogger<T>` + Serilog 的应用侧选择仍然有效；“应用直推 Loki”和“仓库内置 Loki/Grafana”不再是当前设计。

## 取代原因

应用直连具体日志存储会把供应商配置、重试、背压与可用性耦合进业务服务，也会与 stdout 采集造成重复日志。当前实现因此收敛为：

- 业务代码使用 `ILogger<T>`；
- Serilog 输出 Console/stdout，并在配置 OTLP endpoint 时把 `ILogger` 事件交给 OpenTelemetry Logs；
- 部署环境自行选择 Collector、日志采集与存储；
- Trace 可选通过 OTLP 导出；
- Metrics 同时支持 OTLP 导出与 `/metrics` Prometheus 抓取。

Loki、Grafana 或其他后端仍可作为部署选择，但不是 OpenAgent 应用的编译时依赖或默认运行条件。
