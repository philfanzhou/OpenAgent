# CodeAct 代码执行

| 文档 | 内容 |
|---|---|
| [DESIGN.md](./DESIGN.md) | MAF 接入、执行边界、文件生命周期与验证 |
| [部署](../../../integrations/code-runner.md) | Runner 配置、镜像构建、Compose 与运行测试 |

## 共用 Runner 适配

Code 与 Skill 脚本共用 `RunnerToolResult` 完成输出校验、产物登记和结果构造，均反馈
`sandboxReset`、`files`、`skippedFiles`。Workspace 共用错误映射，保留 400/404/409/413 的
领域状态码与修正指引；请求取消向上传播，传输超时返回 `tool_timeout` 信封。

`RunnerClient` 的执行与文件操作共用配置校验、Bearer、deadline、POST 和限长读取。
两类响应均受 `MaxWireBytes` 约束；执行响应额外校验日志长度和产物，Workspace 保留领域异常。
