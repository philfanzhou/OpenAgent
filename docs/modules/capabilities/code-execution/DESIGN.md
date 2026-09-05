# CodeAct 与 Bubblewrap 隔离执行

## 范围

Engine 通过 MAF `AIFunction` 暴露 `execute_code`。模型生成 Python，读取执行结果，并在原有 MAF 工具循环中修正代码。沙箱运行时支持 PPT、Excel、图表和 PDF 生成。

本实现使用独立 Runner 和 Bubblewrap，不依赖 Docker、containerd、KVM 或 Hyperlight，也没有添加第二套 Agent 循环。Bubblewrap 是低层沙箱构造器，隔离强度取决于调用参数，因此参数由 Runner 固定生成，模型和请求均不能覆盖。

## 调用链

```text
AgentExecutor → AgentFactory → CapabilityToolFactory → execute_code
  → RunnerClient → authenticated Runner /v1/execute
    → BubblewrapCodeExecutor → bwrap process sandbox → restricted Python
  ← bounded logs + binary artifacts
  → FileAssetService.UploadAsync / EnsureReferencesAsync
  → model selects publish_files → assistant attachments
```

实现入口：

- `Backend/src/OpenAgent.Core/Capabilities/Code/CodeCapabilitySource.cs`
- `Backend/src/OpenAgent.Runner/BubblewrapCodeExecutor.cs`
- `Backend/src/OpenAgent.Runner/sandbox/execute.py`

必须同时启用 Engine `CodeExecution:Enabled` 与 Agent `config.codeExecution.enabled`。发现和执行工具均经过平台授权。Runner 的运行时、配额和宿主目录只接受管理员配置，不是模型参数。

## 工具契约

`execute_code(code, inputFiles?)` 的每个输入项为 `{fileId, name}`，文件在沙箱中位于 `/input/<name>`。输入必须属于当前租户、当前用户并已被当前会话引用。不可传对象存储键、宿主路径、环境变量或任意 Bubblewrap 参数。`main.py` 为保留名。

返回 `executionId`、`exitCode`、`timedOut`、`stdout`、`stderr` 和文件元数据数组。成功文件登记为当前用户的 FileAsset，并关联当前会话；只有模型调用 `publish_files` 后才发布到 assistant 消息。二进制字节只在 Runner 与 Engine 之间传输，不进入模型上下文。

每次调用创建全新的 namespace、tmpfs 工作区和 Python 进程。`/work` 保存临时工作，`/output` 保存交付文件；沙箱退出前由可信包装脚本验证并编码输出。继续修改文件时，显式将前次返回的 fileId 作为新调用输入。任务间不保留变量、后台进程或可写磁盘。输出只接受普通文件，拒绝符号链接、目录、特殊文件及危险名称。

## 隔离边界

每次执行固定启用：

- 独立 user、PID、IPC、network、UTS namespace；cgroup namespace 在内核支持时启用。
- 沙箱 UID/GID 为 65532；Bubblewrap 的非特权模式默认不向沙箱进程保留 capabilities；同时禁止继续创建 user namespace，并创建新会话。
- 根文件系统从空 tmpfs 构造并整体重挂为只读，只读暴露 `/usr`、固定 Python venv、最小 passwd/group 和字体配置。
- 当前请求输入只读挂载到 `/input`；`/work`、`/output`、`/tmp` 使用独立、限额、退出即销毁的 tmpfs。
- 不挂载宿主 home、源码、服务配置、凭据、设备、Docker Socket、D-Bus socket 或网络。
- `--clearenv` 后只注入固定的 Python/locale/时限变量。
- `prlimit` 限制地址空间、CPU 时间、进程数、打开文件数、单文件大小和 core dump；Runner 并发及 systemd cgroup 再限制节点总量。
- 无宿主 Python 回退；Bubblewrap、user namespace 或 Python venv 不可用时健康检查和执行均失败。

Runner 控制服务属于可信控制面，只允许 Engine 经私网和服务令牌访问，并使用无登录、无 sudo、无 capabilities 的专用用户。systemd 进一步只开放工作目录写权限。

Bubblewrap 与 Docker 一样共享宿主 Linux 内核，不能视为抵御未知内核漏洞的 MicroVM 强边界。它适合受控用户或中等信任的代码执行；若要承载公开、恶意、多租户代码，应把 Runner 节点放进独立 VM，或改用 MicroVM 后端。

沙箱返回的 JSON 同样是不可信输出，Runner 和 Engine 都校验协议大小、文件数量、文件名和字节限额。不能把脚本自行打印的“校验通过”当成隔离证明；集成测试必须检查实际 namespace、挂载、网络、环境和资源限制行为。

## 超时、取消与故障

沙箱包装脚本限制 Python 子进程墙钟时间，Runner 另有外部总截止时间。请求取消或外部截止时间到达时，Runner 终止 bwrap 进程树；`--die-with-parent` 确保 Runner 异常退出时沙箱同时退出。mount namespace 和 tmpfs 由内核自动清理，后台回收器只删除 Runner 崩溃遗留且超过一小时的请求输入目录。

Engine 每请求默认最多执行 8 次代码，MAF 的 MaxTurns 继续约束模型循环。普通脚本错误通过 stderr 返回供模型修正。网络错误、Runner 不可用、超时和取消不会降级为宿主执行。

Runner 请求目前与聊天请求共同存活；不提供断线后继续运行、进程重启后的 Agent 恢复或跨节点调度。

## 文档能力

固定 Python venv 安装 python-pptx、openpyxl、XlsxWriter、pandas、matplotlib、Pillow 和 defusedxml；主机只读运行时提供 LibreOffice 和中文字体。支持生成可编辑 Office 文件，也可在沙箱内调用 LibreOffice 渲染 PDF 后交付。

原有 Skill 指令/资源读取保持可用。文件 Skill 的自动脚本 runner 仍保持禁用；本 PR 不把 Skill 脚本、MCP 服务或业务工具迁入沙箱，也不提供 `call_tool` 回调桥接。文档生成应通过 `execute_code` 完成。

## 验证

Core 测试验证双重开关、授权撤销、跨租户/用户文件拒绝、二进制资产归属，以及确定性 MAF 循环在错误后再次调用执行工具。

Runner 的 Linux 真实测试验证 Bubblewrap 参数与实际边界、网络禁用、宿主文件不可见、任务隔离、内存限制、超时/取消清理、符号链接拒绝、输出截断、tmpfs 容量，以及 PPT/Excel 的生成、重新打开、再次编辑和 PPT 转 PDF。CI 显式安装 Bubblewrap 和固定 Python venv；其他平台会明确跳过真实沙箱测试。

CI 随后发布 Runner 进行真实 HTTP 冒烟，并执行部署脚本、启动强化的 systemd 服务再验收。MAF 联测连接该已安装服务，覆盖 Python 错误反馈、授权 CSV 输入、Excel 生成与再次编辑、文件登记和 `publish_files`。模型与文件存储在此联测中使用确定性替身；不将它标记为外部模型与浏览器聊天端到端验证。
