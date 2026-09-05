# Bubblewrap 代码 Runner 部署

架构、工具参数和安全边界见 [CodeAct 设计](../modules/capabilities/code-execution/DESIGN.md)。Runner 直接运行在 Linux 主机上，不需要 Docker、containerd、KVM 或虚拟机镜像。

## 支持环境

- Ubuntu 24.04 LTS 或同等能力的现代 Linux，支持非特权 user namespace。
- 主机安装发行版提供的 Bubblewrap；不要自行授予 Runner root、sudo 或 Docker Socket 权限。
- .NET 8 SDK 仅在安装脚本发布 Runner 时需要，服务使用自包含产物运行。
- Engine 与 Runner 可以同机部署，也可以通过受防火墙保护的私网 HTTP(S) 通信。

`/health` 会实际创建最小 Bubblewrap namespace。缺少 Bubblewrap、Python 运行时或 user namespace 被禁用时返回 503；执行请求不会回退到宿主 Python。

## 一键安装

在 Ubuntu/Debian 主机的仓库根目录执行：

```bash
sudo deploy/code-runner/install.sh "$(pwd)"
sudo systemctl status openagent-runner --no-pager
curl --fail http://127.0.0.1:5088/health
```

脚本完成以下工作：安装 Bubblewrap、LibreOffice 和中文字体；在 Ubuntu AppArmor 限制启用时加载发行版提供的 `bwrap-userns-restrict` 策略；创建固定版本的 Python venv；创建无登录权限的 `openagent-runner` 用户；发布 Runner；安装并重启强化的 systemd 服务，最后等待健康检查成功。首次安装会在 `/etc/openagent-runner.env` 生成随机服务令牌，该文件权限为 `0600`。

默认只监听 `127.0.0.1:5088`。同机 Engine 配置如下，并使用 `/etc/openagent-runner.env` 中同一个 API key：

```text
CodeExecution__Enabled=true
CodeExecution__Endpoint=http://127.0.0.1:5088
CodeExecution__ApiKey=<Runner__ApiKey>
CodeExecution__RequestTimeoutSeconds=180
```

若 Engine 位于其他主机，将 `ASPNETCORE_URLS` 改为 Runner 的私网地址，并用主机防火墙仅允许 Engine 访问。不要把 Runner 直接暴露到公网。修改后执行 `sudo systemctl restart openagent-runner`。

在 Agent 编辑界面打开“代码执行”，或通过现有 Agent 配置 API 保存：

```json
{"codeExecution":{"enabled":true}}
```

这是 Agent `config` 中的新增片段，保存时需保留其他配置。部署还需应用 `AddCodeExecutionConfiguration` migration；旧 Agent 默认关闭代码执行。

## 配置

| 配置 | 默认 | 含义 |
|---|---|---|
| Engine `CodeExecution:Enabled` | false | 全局执行开关 |
| Engine `CodeExecution:Endpoint` | 空 | Runner 内部 HTTP(S) 地址 |
| Engine `CodeExecution:ApiKey` | 空 | 至少 32 字符的服务令牌 |
| Engine `CodeExecution:RequestTimeoutSeconds` | 180 | 大于 Runner 执行时限和清理余量 |
| Engine `CodeExecution:MaxExecutionsPerRequest` | 8 | 每个聊天请求的代码调用上限 |
| Runner `Runner:BubblewrapPath` | /usr/bin/bwrap | 主机 Bubblewrap 绝对路径 |
| Runner `Runner:PythonPath` | /opt/openagent-code/venv/bin/python | 沙箱 Python venv 绝对路径 |
| Runner `Runner:TimeoutSeconds` | 120 | 单次执行墙钟时限，最大 600 秒 |
| Runner `Runner:MaxConcurrentExecutions` | 2 | Runner 并发上限；超额返回 429 |
| Runner `Runner:MemoryMiB` | 1536 | 每个沙箱进程的地址空间上限；为 LibreOffice 预留虚拟地址空间 |
| Runner `Runner:MaxProcesses` | 64 | 沙箱进程上限，建议结合 systemd `TasksMax` |
| Runner `Runner:WorkspaceMiB` | 128 | `/work` tmpfs 上限 |
| Runner `Runner:WorkspaceRoot` | /var/lib/openagent-runner | 请求输入暂存目录，仅 Runner 可读写 |

固定协议限制：代码 128 KiB；输入/输出各最多 8 个文件，单文件 10 MiB、合计 20 MiB；stdout/stderr 各 32 KiB；`/output` tmpfs 32 MiB，`/tmp` 64 MiB。FileAsset 仍执行自身策略，两层限制取更严格者。

默认 systemd unit 同时给整个 Runner 设置 `MemoryMax=2G`、`TasksMax=256`、`CPUQuota=200%`。这是总量保护；`prlimit` 则负责每次执行的地址空间、CPU 时间、进程、打开文件、产物大小和 core dump 限制。

服务允许 `AF_NETLINK`，供 Bubblewrap 初始化隔离网络命名空间；这不打开沙箱外网。LibreOffice 固定使用 `svp` 无界面后端，不需要 X11 或桌面会话。

## 验证与故障定位

在已安装依赖的 Linux 主机运行真实隔离测试：

```bash
RUN_CODEACT_BWRAP_TESTS=1 \
CODEACT_TEST_PYTHON=/opt/openagent-code/venv/bin/python \
dotnet test Backend/tests/OpenAgent.Runner.Tests/OpenAgent.Runner.Tests.csproj

dotnet publish Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj -c Release -o /tmp/openagent-runner-test
CODEACT_RUNNER_DLL=/tmp/openagent-runner-test/OpenAgent.Runner.dll \
CODEACT_TEST_PYTHON=/opt/openagent-code/venv/bin/python \
python3 scripts/test-codeact-runner.py

# 对正式安装的 systemd 服务执行同样的鉴权、Office/PDF 与清理验收
sudo python3 scripts/test-codeact-runner.py --environment-file /etc/openagent-runner.env
```

真实测试覆盖 user/PID/IPC/UTS/network namespace、嵌套 user namespace 禁用、只读运行时与输入、宿主文件不可见、环境变量清除、tmpfs 容量、内存耗尽、超时/取消、符号链接拒绝、任务间清理，以及 PPT/XLSX/PDF 的生成和再次编辑。

CI 还实际运行安装脚本和 systemd 服务，并启用 `MafLoop_RealRunnerGeneratesEditsAndPublishesAuthorizedArtifact`：通过 MAF 处理一次真实 Python 错误，再读取授权 CSV、生成 Excel、按 fileId 重新编辑并调用 `publish_files`。该联测使用真实 HTTP/Runner/Bubblewrap；模型响应与文件存储使用确定性测试替身，不代表已验证外部模型、生产对象存储和浏览器聊天。

若 `/health` 返回 503，先检查 `journalctl -u openagent-runner`。常见原因是 `bwrap` 或 venv 路径错误，以及云主机/发行版禁用了非特权 user namespace。Ubuntu 的 AppArmor 限制由安装脚本加载发行版 `bwrap-userns-restrict` 策略；不要通过 `kernel.apparmor_restrict_unprivileged_userns=0` 全局关闭保护。

建议初次启用后，再用实际模型执行一次“读取上传 CSV，生成 Excel 和 PPT”，检查聊天附件、内容质量和取消行为。确定性模型测试与 Runner 测试不能替代真实模型端到端验收。
