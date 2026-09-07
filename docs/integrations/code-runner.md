# gVisor CodeAct Runner 部署

架构和安全边界见 [CodeAct 设计](../modules/capabilities/code-execution/DESIGN.md)。Runner 是一个可信 HTTP 控制面，负责为每个 `execute_code` 请求启动一次性 Docker 容器；真正执行 Python 的容器固定使用 gVisor `runsc` runtime。

## 前置条件

gVisor 官方 Docker 流程要求先安装 `runsc`、执行 `runsc install` 注册 Docker runtime，然后重启 Docker daemon。Linux 主机需要支持 gVisor 的架构和内核。开发机可先检查：

```bash
docker info --format '{{json .Runtimes}}'
docker run --rm --runtime=runsc hello-world
```

输出中必须存在 `runsc`。macOS/Windows 上的 Docker Desktop 只有在其 Linux Docker daemon 已提供并注册 `runsc` 时才可用；宿主机有 Docker CLI 不代表 gVisor 已就绪。

## 本地 Compose

本地专用开发环境需要让 Runner 控制面访问 Docker daemon，并让该 daemon 能看到构建的 `openagent-codeact:local` 镜像：

```bash
export OPENAGENT_RUNNER_API_KEY="$(openssl rand -hex 32)"
docker build -t openagent-codeact:local -f docker/codeact/Dockerfile .
docker compose -f docker-compose.storage.yml up -d
docker compose -f docker/preview.compose.yml -f docker/preview.codeact.compose.yml up -d --build
curl --fail http://127.0.0.1:5089/health
```

Compose 默认把 `127.0.0.1:5089` 发布为 Runner 健康端点，并挂载 `/var/run/docker.sock` 供可信 Runner 控制面创建 `runsc` 容器。共享环境应改用专用 rootless daemon：设置 `OPENAGENT_RUNNER_DOCKER_HOST` 和对应的 `DOCKER_SOCKET_GID`，不要把宿主 Docker Socket 暴露给普通业务容器。

健康检查会依次确认 Docker CLI、`runsc` runtime、固定沙箱镜像，并实际启动一个最小 `runsc` 容器。缺一项返回 503；执行请求不会降级为 runc 或宿主 Python。

## 配置

| 配置 | 默认值 | 作用 |
|------|--------|------|
| `Runner:Runtime` | `runsc` | 唯一允许的 Docker runtime |
| `Runner:SandboxImage` | `openagent-codeact:local` | 预装 Office 依赖的固定镜像 |
| `Runner:DockerPath` | `/usr/bin/docker` | Runner 使用的 Docker CLI |
| `Runner:DockerHost` | 空 | 可选的 rootless Docker endpoint |
| `Runner:SandboxPythonPath` | `/opt/openagent-code/venv/bin/python` | 镜像内固定 Python |
| `Runner:TimeoutSeconds` | `120` | 单次 Python 墙钟上限 |
| `Runner:MemoryMiB` | `1536` | 单次容器内存上限 |
| `Runner:WorkspaceMiB` | `128` | `/work` tmpfs 大小 |
| `Runner:MaxProcesses` | `64` | PID 和进程资源上限 |
| `Runner:MaxConcurrentExecutions` | `2` | Runner 并发闸门 |

Engine 侧还需开启 `CodeExecution:Enabled`，并在 Agent 设置中开启“代码执行”。二者缺一时前端仍可聊天，但不会发现 `execute_code` 工具。

## 前端两条验收用例

在工作台选择已开启代码执行的 Agent 和可用模型：

1. 新建会话，点击“生成三行 Excel”示例动作并发送。模型应调用 `execute_code`，生成包含表头和三行数据的 `.xlsx`，重新打开校验后调用 `publish_files`。
2. 上传第一步返回的 `.xlsx`。点击“读取 Excel 生成 PPT”并发送。模型应通过当前会话的 `fileId` 读取 Excel，生成可编辑 `.pptx`，重新打开校验后发布 PPT。

前端上传组件允许 `.xlsx`/`.pptx`，首条消息会复用上传文件所属的 conversationId，Engine 才能执行租户、用户和会话范围校验。

## 验证命令

CI 和 Linux 主机验收：

```bash
RUN_CODEACT_GVISOR_TESTS=1 \
CODEACT_TEST_GVISOR_IMAGE=openagent-codeact:local \
dotnet test Backend/tests/OpenAgent.Runner.Tests/OpenAgent.Runner.Tests.csproj

dotnet publish Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj \
  -c Release -o /tmp/openagent-runner
CODEACT_RUNNER_DLL=/tmp/openagent-runner/OpenAgent.Runner.dll \
CODEACT_TEST_GVISOR_IMAGE=openagent-codeact:local \
python3 scripts/test-codeact-runner.py

dotnet test Backend/tests/OpenAgent.Core.Tests/OpenAgent.Core.Tests.csproj \
  --filter FullyQualifiedName~MafLoop_RealRunner
```

没有 `runsc` 的平台会跳过真实隔离测试，不能将编译、Mock 测试或健康检查失败当作 gVisor E2E 通过。停止本地预览可执行 `docker compose -f docker/preview.compose.yml -f docker/preview.codeact.compose.yml down`；不要删除共享数据库、Redis 或对象存储卷。
