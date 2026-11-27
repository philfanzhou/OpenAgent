# 压测工具

`tools/loadtest/` 提供一套隔离的压测环境，用来测量 Engine 与 Router 的会话发起时延、并发吞吐和资源占用，并对比不同提交的镜像。它不接触开发、预览或生产数据。

## 组成

| 路径 | 内容 |
|---|---|
| `compose/loadtest.compose.yml` | 压测栈：独立的 PostgreSQL、Redis、MinIO，加被测的 Engine、Router 镜像、模型桩和负载发生器 |
| `src/ModelStub` | 确定性的 OpenAI Chat Completions 流式桩，替代真实 LLM；可注入首包延迟、分片速率、错误、挂起和慢响应 |
| `src/LoadDriver` | 负载发生器：固定并发、固定到达率、逐级爬坡、一次性齐发；逐请求记录 SSE 各事件的到达时刻 |
| `scripts/lt.py` | 管理压测栈、写入配置与造数、执行场景、资源采样、评分卡与前后对比 |
| `scripts/summarize.py` | 汇总所有运行的总表 |

工具不加入解决方案，也不参与 CI；模型桩和发生器只在构建压测镜像时编译。

## 运行

在 WSL 的仓库根目录执行。Engine 与 Router 镜像先用 [部署指南](deployment.md) 里的 `scripts/build-images.sh` 构建。

```bash
python3 tools/loadtest/scripts/lt.py stack-up --build   # 起栈；首次加 --build 构建模型桩与发生器
python3 tools/loadtest/scripts/lt.py seed               # 写入指向模型桩的 LLM profile 和 Runtime profile
python3 tools/loadtest/scripts/lt.py run NAME --env MODEL=closed --env CONCURRENCY=4 --env DURATION_SEC=60
python3 tools/loadtest/scripts/lt.py report artifacts/loadtest/results/<目录>
python3 tools/loadtest/scripts/lt.py scorecard LABEL      # 跑固定的一组场景，用于对比镜像
python3 tools/loadtest/scripts/lt.py compare BEFORE AFTER # 输出两个评分卡的前后对比
python3 tools/loadtest/scripts/lt.py idle LABEL              # 只跑空闲场景：保活进程的空闲 CPU、进程回收后每会话落盘
python3 tools/loadtest/scripts/lt.py stack-down -v        # 停栈并删除数据卷
```

`compare` 需要同一台主机上的两个评分卡，所以对比不同提交时先给被测镜像打标签，再分别跑评分卡：

```bash
python3 tools/loadtest/scripts/lt.py apply --engine LT_ENGINE_IMAGE=openagent-engine:before
python3 tools/loadtest/scripts/lt.py scorecard before
python3 tools/loadtest/scripts/lt.py apply --engine LT_ENGINE_IMAGE=openagent-engine:after
python3 tools/loadtest/scripts/lt.py scorecard after
python3 tools/loadtest/scripts/lt.py compare before after
```

## 参数

- 环境变量 `LT_PROJECT` 是 compose 项目名，默认 `openagent-loadtest`。Engine 容器内存上限默认 7 GB（`LT_ENGINE_MEM_LIMIT`），超限时 OOM 只发生在容器内，用来保护同机的其他服务。
- `apply --engine KEY=VALUE` 持久设置 Engine 的环境变量，之后每次启动都带上：`LT_MAX_TURNS`、`LT_KEEPALIVE`、`LT_MAX_KEPT`、`LT_REQ_TIMEOUT`、`LT_RETENTION_DAYS`、`LT_LOG_USAGE` 分别对应 `OpenCode:MaxConcurrentTurns` 等运行时参数；`apply --router KEY=VALUE` 同理设置 Router，例如 `LT_ROUTER_IMAGE=openagent-router:标签`。
- 镜像没变时 `apply` 不会重建容器。要观察只在启动时发生的行为（例如会话状态的首轮回收），需要显式重启容器，或等下一个周期。
- `run --env KEY=VALUE` 传给发生器：
  - `MODEL`：`closed`（固定并发）、`open`（固定到达率）、`ramp`（逐级爬坡，`RAMP=1:40,2:40,4:60`）、`burst`（一次性齐发）。
  - `CONCURRENCY`、`RATE`、`DURATION_SEC`、`MAX_REQUESTS`、`BURST_SIZE`。
  - `CONV_MODE`：`new` 每个请求新会话；`worker` 每个并发复用自己的会话（追问路径）；`shared` 全部使用同一会话。
  - `ABORT_AFTER_CONTENT=N` 在第 N 个内容分片后中止，`CANCEL_API=1` 时改为调用取消接口。
  - `OP=get` 加 `GET_PATH` 压测只读接口；`--via router` 经 Router 而不是直连 Engine。
- `--stub KEY=VALUE` 设置模型桩：`ttft_ms`、`tokens`、`interval_ms`、`error_rate`、`hang_rate`、`slow_rate`、`prompt_tokens`。每次运行都会下发完整参数，不会残留上一次的故障注入。

## 口径与结果

- 成功指收到 `done` 且没有 `error` 事件；时延只统计成功请求；首 token 指第一个 `content` 事件。
- 每次运行一个结果目录（已被 gitignore）：逐请求的 `requests-*.csv`、资源与指标采样 `resources.ndjson` 和 `metrics.ndjson`、Engine 与 Router 日志，以及记录被测镜像和运行参数的 `manifest.json`。
- Engine 与 Router 的环境变量和 [并行预览](parallel-previews.md) 的 compose 对齐，但不导出 OTLP，避免把压测数据写进共享的观测后端。

## 局限

模型桩的轮次很短，会放大平台固定开销在时延中的占比；真实模型的回答更长，每台主机的吞吐更接近「并发上限 ÷ 平均轮次时长」。数值属于运行压测栈的那台主机，与其他负载共享 CPU 和内存时偏保守。
