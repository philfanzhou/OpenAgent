# CodeAct 与 gVisor 隔离执行

## 范围

Engine 通过 MAF `AIFunction` 暴露 `execute_code`。模型生成 Python，读取执行结果，并在原有 MAF 工具循环中修正代码。沙箱预装 Office Python 依赖，可生成并重新打开 Excel、PPT 和 PDF。

隔离执行由独立 Runner 控制面负责。Runner 为每个请求创建一次性 Docker 容器，并固定使用 `runsc` runtime。请求不能选择 runtime、镜像、挂载、网络或资源参数；Runner 发现 gVisor runtime、沙箱镜像或 Docker daemon 不可用时会失败，不会回退到宿主 Python 或 runc。

## 调用链

```text
AgentExecutor → AgentFactory → CapabilityToolFactory → execute_code
  → RunnerClient → authenticated Runner /v1/execute
    → GVisorCodeExecutor → docker create --runtime=runsc
      → one-shot Office Python container
  ← bounded logs + binary artifacts
  → FileAssetService.UploadAsync / EnsureReferencesAsync
  → model calls publish_files → assistant attachments
```

实现入口：

- `Backend/src/OpenAgent.Core/Capabilities/Code/CodeCapabilitySource.cs`
- `Backend/src/OpenAgent.Runner/GVisorCodeExecutor.cs`
- `Backend/src/OpenAgent.Runner/DockerProcess.cs`
- `Backend/src/OpenAgent.Runner/sandbox/execute.py`

Engine `CodeExecution:Enabled` 和 Agent `config.codeExecution.enabled` 必须同时为 `true`。发现和执行工具都经过服务端授权。Runner runtime、镜像、Docker endpoint 和配额属于部署配置，不能由模型传入。

## 工具和文件资产

`execute_code(code, inputFiles?)` 的输入项为 `{fileId, name}`。Engine 只读取当前租户、当前用户且已由当前会话引用的 `FileAsset`，然后以只读语义复制到容器 `/input/<name>`。`main.py` 是保留名。

沙箱包装脚本将 `/output` 中的普通文件编码到有大小上限的 JSON。Engine 校验文件名、扩展名、数量、字节数和日志后，把产物登记为当前会话的 Agent `FileAsset`。二进制内容不会进入模型上下文，只有模型调用 `publish_files` 后才出现在 assistant 消息中。下一次编辑必须显式传入上一次返回的 `fileId`。

## gVisor 边界

每次容器固定启用：

- Docker `--runtime runsc`、`--network none`、只读根文件系统和非 root UID/GID 65532。
- `--cap-drop ALL`、`no-new-privileges`、PID/内存/CPU/文件大小/打开文件限制。
- 仅有临时的 `/input`、`/work`、`/output`、`/tmp` 和 `/run` tmpfs。不会挂载宿主源码、home、凭据、设备、D-Bus 或 Docker Socket。
- 镜像内固定 Python venv、LibreOffice 和 Office 库。容器不继承 Runner 的环境变量，不允许安装包或访问互联网。
- Runner 和 Docker CLI 同时校验响应上限，并在请求结束后删除容器和暂存目录。

Runner 本身是可信控制面，连接 Docker daemon 的权限不能授予不可信代码。共享部署应为 Runner 配置专用 rootless Docker daemon 的 socket；本地 Compose 示例默认挂载 Docker socket，仅用于专用开发环境，不能与不可信服务共用。

gVisor 仍运行在 Linux 主机内核之上，不能等同于 MicroVM，也不能抵御宿主内核或 Docker daemon 的失陷。公开多租户部署应将 Runner 放在专用 VM 或进一步采用 MicroVM 后端。

## 验收

Linux 主机必须先注册 gVisor 的 `runsc` runtime 并构建固定镜像。真实 Runner 测试覆盖认证、文件输入、环境清理、无网络、只读根、并发隔离、超时清理，以及三行 Excel 生成和 Excel → PPT/PDF 生成。MAF 测试再覆盖错误反馈、授权文件读取、产物登记、按 fileId 编辑和 `publish_files`。

当前前端文件选择器支持 `.xlsx` 和 `.pptx`，并提供两个 CodeAct 示例动作。点击动作只会填入自然语言请求，真正执行仍经过前端聊天流、Engine MAF 工具循环和 gVisor Runner。
