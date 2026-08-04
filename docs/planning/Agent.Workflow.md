# Agent.Workflow 状态

## 当前状态

`Agent.Workflow` 是规划中的复杂流程编排服务，当前仓库没有对应项目、程序集、容器或可部署
端点。任何把它描述为“已实现”的文档都应理解为目标架构，而不是当前运行能力。

## Router 兼容行为

Router 仍保留 `workflow` 意图和 `RouterSettings:Routing:WorkflowEndpoint`，用于保护现有配置契约
并为未来接入保留扩展点。该兼容入口不代表 Workflow 服务存在：

- 当前生产请求应路由到已注册的 Engine；
- 未部署 Workflow 时，不应发送 `workflow` 意图；
- `appsettings.Development.json` 中的地址只用于兼容/本地占位，不能作为服务可用性证据；
- 新实现接入前必须补项目边界、鉴权、健康检查、服务发现、超时降级和集成测试。

## 文档约定

涉及 Workflow 的需求必须标记为“规划中”或“兼容入口”。当前服务拓扑以根 `README.md`、
`AGENTS.md` 和实际 `.csproj` 为准。
