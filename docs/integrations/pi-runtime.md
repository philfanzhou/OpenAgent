# Pi ACP 运行时

OpenCode 是默认运行时。通过 Engine Dockerfile 的 `runtime-with-pi` 构建目标可获得带 `@openma/pi-acp@0.1.8` 和 Pi 1.6.0 的试点镜像。设置 `Pi:Enabled=true` 和 `Pi:AllowedTenants` 后，允许名单中的租户可选择 Pi。独立部署可指定 `Pi:Executable` 为适配器的绝对路径，使用 Node.js 22.19 或更高版本。Engine 校验适配器版本和 HTTP MCP 能力后执行本轮。

用户在 Chat 顶栏选择 Pi 后发起新轮次。切换沿用平台会话 ID、已提交历史、Agent 配置与文件引用。Pi 每轮新建 ACP 会话并读取平台历史，以文本和图像上下文开始本轮。工具结果作为历史提供，历史工具调用无需再执行。实际运行时名称由 SSE 告知 Chat。

每轮使用私有工作区与进程配置，模型连接沿用用户在平台选择的模型。平台工具通过本轮工具桥授权，外部 MCP 采用支持的 HTTP 传输。宿主通过 `Pi:AllowedNativeTools` 显式放行所需的原生工具，初始列表为空。Pi 仓库的工作区约束属于应用层措施，原生 shell 工具的主机权限取决于运行进程的身份与环境。

已用真实适配器和回环模型校验建会、关闭、指令、平台历史重放、HTTP MCP 工具桥、图片输入、流式响应和取消。在同一个 Engine 会话从 OpenCode 切换到 Pi 时，历史和 Agent 得到保留，历史工具调用不重复执行。上线验收仍包含实际模型、文件交付、压缩后续聊及审计归集。回滚时关闭 `Pi:Enabled` 并重启 Engine。
