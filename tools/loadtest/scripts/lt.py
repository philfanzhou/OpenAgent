#!/usr/bin/env python3
"""OpenAgent 压测工具：压测栈管理、配置造数、场景执行、资源采样与结果汇总。在 WSL 中运行。

  python3 tools/loadtest/scripts/lt.py stack-up [--build] [--multi]
  python3 tools/loadtest/scripts/lt.py stack-down [-v]
  python3 tools/loadtest/scripts/lt.py status
  python3 tools/loadtest/scripts/lt.py apply [--engine KEY=VALUE ...] [--router KEY=VALUE ...] [--multi] [--settle S]
  python3 tools/loadtest/scripts/lt.py seed [--smoke [--via router]]
  python3 tools/loadtest/scripts/lt.py stub [--reset] [--ttft-ms N] [--tokens N] [--interval-ms N] [--error-rate R]
                                            [--error-status C] [--hang-rate R] [--slow-rate R] [--slow-ttft-ms N]
  python3 tools/loadtest/scripts/lt.py run NAME [--via router] [--env KEY=VALUE ...] [--stub KEY=VALUE ...]
                                                [--interval S] [--settle S]
  python3 tools/loadtest/scripts/lt.py sample --out DIR [--duration S] [--interval S]
  python3 tools/loadtest/scripts/lt.py report RUN_DIR [...]

apply 写入的环境变量保存在 artifacts/loadtest/state/，之后的每次 compose 调用都会带上，
避免下次 up 时被默认值覆盖回去。--env 透传给容器内的负载发生器（变量含义见 src/LoadDriver）。
"""

import argparse
import contextlib
import io
import base64
import csv
import datetime
import json
import os
import pathlib
import re
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

REPO = pathlib.Path(__file__).resolve().parents[3]
COMPOSE = REPO / "tools/loadtest/compose/loadtest.compose.yml"
PROJECT = os.environ.get("LT_PROJECT", "openagent-loadtest")
RESULTS = REPO / "artifacts/loadtest/results"
STATE = REPO / "artifacts/loadtest/state"
SCORECARD = REPO / "artifacts/loadtest/scorecard"
SESSION_ROOT = "/home/app/openagent-sessions"
ENGINE_URL = "http://localhost:5310"
ROUTER_URL = "http://localhost:5311"
STUB_URL = "http://localhost:5312"
ROUTER_TARGET = "http://router:5001"
ENGINE_TARGET = "http://engine:5208"
AUTH = "Basic " + base64.b64encode(b"admin:admin").decode()
LLM_PROFILE_ID = "load-stub"
AGENT_ID = "default"
TURN_TIMING = re.compile(
    r"process=(\d+)ms sessionNew=(\d+)ms prompt=(\d+)ms firstUpdate=(\d+)ms "
    r"firstToken=(\d+)ms stream=(\d+)ms close=(\d+)ms total=(\d+)ms")
METRIC_LINE = re.compile(r"^(?P<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{(?P<labels>[^}]*)\})?\s+(?P<value>[^\s]+)")
METRIC_LABEL = re.compile(r'(\w+)="((?:[^"\\]|\\.)*)"')
DROPPED_LABELS = {"server_address", "server_port", "network_protocol_version", "url.scheme", "url.query"}
FLATTEN = {
    "http_server_active_requests": "active_requests",
    "http_server_request_duration_seconds_count": "http_count",
    "kestrel_active_connections": "kestrel_active_connections",
    "kestrel_queued_connections": "kestrel_queued_connections",
    "openagent_engine_executions_total": "exec",
    "openagent_engine_agent_calls_total": "agent_calls",
    "openagent_router_rate_limit_decisions_total": "rate_limit",
    "openagent_router_forwarding_failures_total": "forward_fail",
    "openagent_router_requests_total": "requests",
    "openagent_router_sse_duration_seconds_count": "sse_count",
}
RESOURCE_KEYS = [
    "host_load1", "host_mem_avail_mb", "engine_cpu", "engine_mem_mb", "engine_rss_mb", "opencode_count",
    "opencode_rss_mb", "router_cpu", "router_mem_mb", "pg_cpu", "pg_mem_mb", "redis_cpu", "redis_mem_mb",
    "stub_cpu", "stub_mem_mb", "generator_cpu", "generator_mem_mb", "pg_connections", "redis_keys", "ws_mb", "state_mb",
]


# ---------------------------------------------------------------- 基础工具

def now_iso(offset_seconds=0):
    moment = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=offset_seconds)
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def run(cmd, env=None, check=True, timeout=None):
    result = subprocess.run([str(item) for item in cmd], env=env, text=True, capture_output=True, timeout=timeout)
    if check and result.returncode != 0:
        sys.stderr.write(result.stdout or "")
        sys.stderr.write(result.stderr or "")
        raise SystemExit(f"command failed ({result.returncode}): {' '.join(str(item) for item in cmd)}")
    return result


def load_env(name):
    values = {}
    path = STATE / f"{name}.env"
    if path.exists():
        for line in path.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key] = value
    return values


def save_env(name, values):
    STATE.mkdir(parents=True, exist_ok=True)
    (STATE / f"{name}.env").write_text("".join(f"{key}={value}\n" for key, value in sorted(values.items())))


def compose_env():
    env = os.environ.copy()
    for name in ("engine", "router"):
        env.update(load_env(name))
    return env


def compose(*args, check=True, timeout=None):
    return run(["docker", "compose", "-f", COMPOSE, "-p", PROJECT, *args], env=compose_env(), check=check, timeout=timeout)


def container(name):
    return f"{PROJECT}-{name}-1"


def container_running(name):
    result = run(["docker", "inspect", "--format", "{{.State.Running}}", container(name)], check=False)
    return result.returncode == 0 and result.stdout.strip() == "true"


def engines():
    return ["engine"] + (["engine2"] if container_running("engine2") else [])


def http(method, url, body=None, headers=None, timeout=20, auth=True):
    request_headers = {"Authorization": AUTH} if auth else {}
    if headers:
        request_headers.update(headers)
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, method=method, headers=request_headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def wait_ready(ready_url, label, timeout=240):
    deadline = time.time() + timeout
    last = "no response"
    url = ready_url
    while time.time() < deadline:
        try:
            status, text = http("GET", ready_url, timeout=5, auth=False)
            last = f"HTTP {status} {text.strip()}"
            if status == 200:
                print(f"ready: {label} -> {last}")
                return
        except (OSError, urllib.error.URLError) as error:
            last = repr(error)
        time.sleep(3)
    try:
        _, report = http("GET", url.rsplit("/", 1)[0] + "/report", timeout=10, auth=False)
    except (OSError, urllib.error.URLError):
        report = "no report"
    raise SystemExit(f"{label} is not ready: {last}; report: {report[:1200]}")


STUB_KEYS = {
    "ttft_ms": "ttftMs", "tokens": "tokens", "interval_ms": "tokenIntervalMs", "error_rate": "errorRate",
    "error_status": "errorStatus", "hang_rate": "hangRate", "slow_rate": "slowRate", "slow_ttft_ms": "slowTtftMs",
    "prompt_tokens": "promptTokens",
}


STUB_DEFAULTS = {
    "ttft_ms": 300, "tokens": 32, "interval_ms": 25, "error_rate": 0, "error_status": 500,
    "hang_rate": 0, "slow_rate": 0, "slow_ttft_ms": 60000, "prompt_tokens": 0,
}


def stub_payload(values):
    """把命令行的下划线命名转换成模型桩接口的驼峰命名；传错名字时直接报错，避免故障注入静默失效。"""
    payload = {}
    for key, value in values.items():
        if key not in STUB_KEYS:
            raise SystemExit(f"unknown model stub option: {key}")
        payload[STUB_KEYS[key]] = float(value) if key.endswith("_rate") else int(value)
    return payload


def verify_stub_config(payload, returned):
    for key, value in payload.items():
        if float(returned.get(key, "nan")) != float(value):
            raise SystemExit(f"model stub did not apply {key}={value}: {returned}")


def parse_kv(items):
    values = {}
    for item in items or []:
        if "=" not in item:
            raise SystemExit(f"expected KEY=VALUE, got {item!r}")
        key, value = item.split("=", 1)
        values[key] = value
    return values


def percentile(values, fraction):
    ordered = sorted(values)
    if not ordered:
        return None
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * fraction
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    weight = position - lower
    return ordered[lower] * (1 - weight) + ordered[upper] * weight


def fmt(value, digits=0):
    return "-" if value is None else f"{value:.{digits}f}"


# ---------------------------------------------------------------- 栈管理

def cmd_stack_up(args):
    if args.build:
        print("==> building modelstub + generator")
        compose("build", "modelstub", "generator", timeout=1800)
    # 基础设施就绪后先建桶，再起 Engine：Engine 的 /ready 会检查桶是否存在。
    print("==> compose up infrastructure")
    compose("up", "-d", "--wait", "postgres", "redis", "minio", "modelstub", timeout=600)
    compose("run", "--rm", "-T", "minio-init", timeout=300)
    wait_ready(STUB_URL + "/healthz", "modelstub", timeout=60)
    services = ["engine", "router"]
    if args.multi:
        services.append("engine2")
    print("==> compose up:", " ".join(services))
    compose("up", "-d", "--wait", *services, timeout=600)
    wait_ready(ENGINE_URL + "/health/ready", "engine")
    wait_ready(ROUTER_URL + "/health/ready", "router")
    cmd_status(args)


def cmd_stack_down(args):
    print("==> compose down" + (" -v" if args.volumes else ""))
    compose("down", *(["-v"] if args.volumes else []), timeout=300)


def cmd_status(_args):
    result = compose("ps", "-a", "--format", "json")
    print(f"{'SERVICE':<12} {'STATE':<10} {'HEALTH':<10} STATUS")
    for line in sorted(item for item in result.stdout.splitlines() if item.strip()):
        row = json.loads(line)
        print(f"{row.get('Service', ''):<12} {row.get('State', ''):<10} {str(row.get('Health') or ''):<10} {row.get('Status', '')}")
    for label, url in (("engine", ENGINE_URL), ("router", ROUTER_URL)):
        try:
            status, text = http("GET", url + "/health/report", auth=False, timeout=10)
            if status == 200:
                report = json.loads(text)
                summary = ", ".join(f"{item['key']}={item['status']}" for item in report["items"])
                print(f"{label} /health/report: {report['status']} ({report['totalDurationMs']} ms) {summary}")
            else:
                print(f"{label} /health/report: HTTP {status}")
        except (OSError, ValueError) as error:
            print(f"{label} /health/report: {error!r}")
    status, text = http("GET", STUB_URL + "/stats", auth=False)
    print(f"modelstub /stats: HTTP {status} {text[:600]}")
    print("engine overrides:", json.dumps(load_env("engine")))
    print("router overrides:", json.dumps(load_env("router")))


def cmd_apply(args):
    if args.engine:
        values = load_env("engine")
        values.update(parse_kv(args.engine))
        save_env("engine", values)
    if args.router:
        values = load_env("router")
        values.update(parse_kv(args.router))
        save_env("router", values)
    services = [name for name in ("engine", "router") if getattr(args, name)]
    if args.multi and not container_running("engine2"):
        services.append("engine2")
    if services:
        print("==> compose up:", " ".join(services))
        compose("up", "-d", "--wait", *services, timeout=600)
    wait_ready(ENGINE_URL + "/health/ready", "engine")
    wait_ready(ROUTER_URL + "/health/ready", "router")
    for name in engines():
        text = run(["docker", "inspect", "--format", "{{json .Config.Env}}", container(name)]).stdout
        effective = sorted(item for item in json.loads(text) if item.startswith(("OpenCode__", "Serilog__")))
        print(f"{name} effective runtime env:")
        for item in effective:
            print("  ", item)
    if args.settle:
        print(f"==> settle {args.settle}s")
        time.sleep(args.settle)


# ---------------------------------------------------------------- 配置造数

def cmd_seed(args):
    profile = {
        "name": "load-stub", "format": "OpenAIChatCompletions", "modelId": "load-stub",
        "endpoint": "http://modelstub:8080/v1", "apiKey": "local-test", "temperature": 0.7,
        "contextTokens": 100000, "modality": "Text",
    }
    status, text = http("PUT", f"{ENGINE_URL}/api/v1/admin/llm/{LLM_PROFILE_ID}", profile)
    print(f"llm profile {LLM_PROFILE_ID}: HTTP {status} {text[:240]}")
    if status != 200:
        raise SystemExit("failed to save LLM profile")
    runtimes = {
        "load-default": {"name": "load-default", "provider": "OpenCode", "turnTimeoutSeconds": 1800},
        "load-timeout-30s": {"name": "load-timeout-30s", "provider": "OpenCode", "turnTimeoutSeconds": 30},
    }
    for runtime_id, body in runtimes.items():
        status, text = http("PUT", f"{ENGINE_URL}/api/v1/admin/runtimes/{runtime_id}", body)
        print(f"runtime {runtime_id}: HTTP {status} {text[:200]}")
        if status != 200:
            raise SystemExit("failed to save runtime profile")
    status, text = http("GET", f"{ENGINE_URL}/api/v1/agent/agents")
    print(f"agents: HTTP {status} {text[:400]}")
    if status != 200:
        raise SystemExit("agent catalog is not available")
    if args.smoke:
        reset = stub_payload(STUB_DEFAULTS)
        status, _ = http("POST", STUB_URL + "/config", reset, auth=False)
        if status != 200:
            raise SystemExit("failed to reset model stub config")
        smoke(args.via)


def smoke(via="engine"):
    base = ENGINE_URL if via == "engine" else ROUTER_URL
    conversation_id = str(uuid.uuid4())
    body = {
        "message": "你好，请用一句话介绍你自己。", "fileIds": [],
        "context": {"agentId": AGENT_ID, "llmProfileId": LLM_PROFILE_ID, "conversationId": conversation_id},
    }
    request = urllib.request.Request(
        base + "/api/v1/agent/chat/stream", data=json.dumps(body).encode(), method="POST",
        headers={"Authorization": AUTH, "Content-Type": "application/json", "Accept": "text/event-stream",
                 "X-Conversation-Id": conversation_id})
    started = time.perf_counter()
    timeline = []
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            print(f"smoke via {via}: HTTP {response.status} {response.headers.get('Content-Type')}")
            event = "?"
            for raw in response:
                line = raw.decode("utf-8", "replace").rstrip("\r\n")
                elapsed = (time.perf_counter() - started) * 1000
                if line.startswith("event:"):
                    event = line[6:].strip()
                elif line.startswith("data:"):
                    timeline.append((elapsed, event, line[5:].strip()[:150]))
                elif line.startswith(":"):
                    timeline.append((elapsed, "comment", line[:80]))
    except urllib.error.HTTPError as error:
        print(f"smoke via {via}: HTTP {error.code} {error.read().decode('utf-8', 'replace')[:400]}")
        return []
    for elapsed, event, data in timeline:
        print(f"  {elapsed:9.1f} ms  {event:<14} {data}")
    status, text = http("GET", STUB_URL + "/stats", auth=False)
    print("modelstub /stats:", text[:500])
    return timeline


def cmd_stub(args):
    if args.reset:
        status, _ = http("POST", STUB_URL + "/stats/reset", body={}, auth=False)
        print(f"modelstub stats reset: HTTP {status}")
    config = {}
    for option in ("ttft_ms", "tokens", "interval_ms", "error_rate", "error_status", "hang_rate", "slow_rate",
                   "slow_ttft_ms"):
        value = getattr(args, option)
        if value is not None:
            config[option] = value
    if config:
        payload = stub_payload(config)
        status, text = http("POST", STUB_URL + "/config", payload, auth=False)
        print(f"modelstub config: HTTP {status} {text}")
        if status != 200:
            raise SystemExit("failed to apply model stub config")
        verify_stub_config(payload, json.loads(text))
    status, text = http("GET", STUB_URL + "/stats", auth=False)
    print("modelstub /stats:", text)


def psql(sql):
    result = subprocess.run(["docker", "exec", "-i", container("postgres"), "psql", "-v", "ON_ERROR_STOP=1",
                             "-U", "openagent", "-d", "openagent", "-At"], input=sql, text=True, capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"psql failed: {result.stderr[:600]}")
    return result.stdout


def cmd_seed_data(args):
    """直接向隔离的压测库写入历史会话（每条含一问一答），模拟数据量；另建若干长会话用于详情与续聊测试。"""
    count, long_count, long_turns = args.count, args.long_count, args.long_turns
    sql = f"""
insert into openagent.conversations ("ConversationId","TenantId","UserId","AgentId","TraceId","Version","Status",
  "CreatedAt","UpdatedAt","LastMessageAt","MessageCount","Title","IsDeletedByUser","ContextSummariesJson","Type")
select md5('seed' || i), 'development', 'admin', 'default', md5('trace' || i), 3, 1,
  now() - (i * interval '37 seconds'), now() - (i * interval '37 seconds') + interval '6 seconds',
  now() - (i * interval '37 seconds') + interval '6 seconds', 2,
  '压测造数会话 ' || i || case when i % 50 = 0 then ' zephyr' else '' end, false, '[]'::jsonb, 0
from generate_series(1, {count}) i
on conflict do nothing;

insert into openagent.conversation_messages ("MessageId","ConversationId","Sequence","Role","Content","Timestamp","TraceId")
select md5(c."ConversationId" || '-1'), c."ConversationId", 1, 'user',
  '压测造数提问：请介绍这个主题的背景。' || case when c."Title" like '%zephyr%' then ' zephyr' else '' end,
  c."LastMessageAt" - interval '1 millisecond', c."TraceId"
from openagent.conversations c where c."Title" like '压测造数会话 %'
on conflict do nothing;

insert into openagent.conversation_messages ("MessageId","ConversationId","Sequence","Role","Content","Timestamp",
  "ModelId","TraceId")
select md5(c."ConversationId" || '-2'), c."ConversationId", 2, 'assistant',
  '这是压测造数的助手回复。' || repeat('负载测试样本文本用于检索性能评估。', 20),
  c."LastMessageAt", 'load-stub', c."TraceId"
from openagent.conversations c where c."Title" like '压测造数会话 %'
on conflict do nothing;

insert into openagent.conversations ("ConversationId","TenantId","UserId","AgentId","TraceId","Version","Status",
  "CreatedAt","UpdatedAt","LastMessageAt","MessageCount","Title","IsDeletedByUser","ContextSummariesJson","Type")
select md5('long' || k), 'development', 'admin', 'default', md5('longtrace' || k), 2 * {long_turns} + 1, 1,
  now() - (k * interval '5 minutes'), now(), now(), 2 * {long_turns}, '压测长会话 ' || k, false, '[]'::jsonb, 0
from generate_series(1, {long_count}) k
on conflict do nothing;

insert into openagent.conversation_messages ("MessageId","ConversationId","Sequence","Role","Content","Timestamp","TraceId")
select md5('longmsg' || k || '-' || m), md5('long' || k), m, case when m % 2 = 1 then 'user' else 'assistant' end,
  '长会话 ' || k || ' 第 ' || m || ' 条消息。' || repeat('用于评估长会话加载与续聊的内容。', 12),
  now() - ((2 * {long_turns} - m) * interval '30 seconds'), md5('longtrace' || k)
from generate_series(1, {long_count}) k cross join generate_series(1, 2 * {long_turns}) m
on conflict do nothing;

analyze openagent.conversations;
analyze openagent.conversation_messages;
select 'conversations=' || count(*) from openagent.conversations;
select 'messages=' || count(*) from openagent.conversation_messages;
select 'messages_size=' || pg_size_pretty(pg_total_relation_size('openagent.conversation_messages'));
select 'long_ids=' || string_agg("ConversationId", ',') from openagent.conversations where "Title" like '压测长会话 %';
"""
    print(psql(sql).strip())


# ---------------------------------------------------------------- 资源采样

def to_mb(text):
    match = re.match(r"([\d.]+)\s*([KMG]iB)", text.strip())
    if not match:
        return None
    return round(float(match.group(1)) * {"KiB": 1 / 1024, "MiB": 1, "GiB": 1024}[match.group(2)], 1)


def docker_stats():
    result = run(["docker", "stats", "--no-stream", "--format", "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.PIDs}}"],
                 timeout=40)
    stats = {}
    for line in result.stdout.splitlines():
        parts = line.split("|")
        if len(parts) != 4:
            continue
        name, cpu, memory, pids = parts
        if not name.startswith(PROJECT + "-"):
            continue
        service = name[len(PROJECT) + 1:].rsplit("-", 1)[0]
        service = "generator" if service.startswith("generator") else service
        stats[service] = {
            "cpu": round(float(cpu.strip().rstrip("%") or 0), 1),
            "mem_mb": to_mb(memory.split("/")[0]),
            "pids": int(pids.strip() or 0),
        }
    return stats


def engine_processes(name):
    """按进程类型汇总 Engine 容器内的 RSS：Engine 自身（dotnet）与 OpenCode 子进程分开计。"""
    result = run(["docker", "top", container(name), "-eo", "pid,rss,args"], check=False, timeout=20)
    processes = {}
    for line in result.stdout.splitlines()[1:]:
        parts = line.split(None, 2)
        if len(parts) != 3:
            continue
        command = parts[2].split()
        if "OpenAgent.Engine.Host.dll" in parts[2]:
            kind = "engine"
        elif command and pathlib.PurePosixPath(command[0]).name.startswith("opencode"):
            kind = "opencode"
        else:
            continue
        entry = processes.setdefault(kind, {"count": 0, "rss_mb": 0.0})
        entry["count"] += 1
        entry["rss_mb"] += int(parts[1]) / 1024
    for entry in processes.values():
        entry["rss_mb"] = round(entry["rss_mb"], 1)
    return processes


def parse_metrics(text):
    series = {}
    for line in text.splitlines():
        if not line or line[0] == "#":
            continue
        match = METRIC_LINE.match(line)
        if not match:
            continue
        labels = {key: value for key, value in METRIC_LABEL.findall(match.group("labels") or "")
                  if key not in DROPPED_LABELS}
        try:
            value = float(match.group("value"))
        except ValueError:
            continue
        key = match.group("name")
        if labels:
            key += "{" + ",".join(f"{k}={v}" for k, v in sorted(labels.items())) + "}"
        series[key] = series.get(key, 0.0) + value
    return series


def flatten(series):
    """把关注的序列摊平成标量列；标签里的非法字符替换成下划线。"""
    flat = {}
    for key, value in series.items():
        name, _, labels = key.partition("{")
        prefix = FLATTEN.get(name)
        if prefix is None:
            continue
        labels = labels.rstrip("}")
        if name == "http_server_request_duration_seconds_count" and "chat/stream" not in labels:
            continue
        if name == "http_server_active_requests":
            flat[prefix] = round(flat.get(prefix, 0.0) + value, 1)
            continue
        label_key = re.sub(r"[^A-Za-z0-9]+", "_", labels.replace("=", "_")).strip("_") if labels else ""
        flat[f"{prefix}_{label_key}" if label_key else prefix] = round(value, 3)
    return flat


class Sampler:
    def __init__(self, out_dir, interval=3.0):
        self.out_dir = pathlib.Path(out_dir)
        self.interval = interval
        self.stop_event = threading.Event()
        self.thread = None

    def start(self):
        self.out_dir.mkdir(parents=True, exist_ok=True)
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()

    def stop(self):
        self.stop_event.set()
        if self.thread:
            self.thread.join()

    def _loop(self):
        tick = 0
        with open(self.out_dir / "resources.ndjson", "a") as resources, \
                open(self.out_dir / "metrics.ndjson", "a") as metrics_file:
            while not self.stop_event.is_set():
                started = time.time()
                try:
                    row, metrics = self.sample(tick)
                except Exception as error:  # 采样失败不能打断压测，记一行后继续。
                    row, metrics = {"error": repr(error)[:200]}, {}
                row["ts"] = round(started, 3)
                row["tick"] = tick
                resources.write(json.dumps(row) + "\n")
                resources.flush()
                metrics_file.write(json.dumps({"ts": row["ts"], **metrics}) + "\n")
                metrics_file.flush()
                tick += 1
                self.stop_event.wait(max(0.0, self.interval - (time.time() - started)))

    def sample(self, tick):
        row, metrics = {}, {}
        with open("/proc/loadavg") as file:
            row["host_load1"] = round(float(file.read().split()[0]), 2)
        with open("/proc/meminfo") as file:
            info = dict(line.split(":", 1) for line in file)
            row["host_mem_avail_mb"] = round(int(info["MemAvailable"].split()[0]) / 1024)
        stats = docker_stats()
        for service, prefix in (("engine", "engine"), ("engine2", "engine2"), ("router", "router"),
                                ("postgres", "pg"), ("redis", "redis"), ("modelstub", "stub"),
                                ("generator", "generator")):
            entry = stats.get(service)
            if entry:
                row[f"{prefix}_cpu"] = entry["cpu"]
                row[f"{prefix}_mem_mb"] = entry["mem_mb"]
                row[f"{prefix}_pids"] = entry["pids"]
        row["opencode_count"] = 0
        row["opencode_rss_mb"] = 0.0
        for name in engines():
            processes = engine_processes(name)
            engine_rss = processes.get("engine", {}).get("rss_mb", 0.0)
            opencode = processes.get("opencode", {})
            row[f"{name}_rss_mb"] = engine_rss
            row[f"{name}_opencode_count"] = opencode.get("count", 0)
            row[f"{name}_opencode_rss_mb"] = opencode.get("rss_mb", 0.0)
            row["opencode_count"] += opencode.get("count", 0)
            row["opencode_rss_mb"] = round(row["opencode_rss_mb"] + opencode.get("rss_mb", 0.0), 1)
        if container_running("postgres"):
            query = "select count(*) from pg_stat_activity where datname = 'openagent'"
            output = run(["docker", "exec", container("postgres"), "psql", "-U", "openagent", "-d", "openagent", "-Atc",
                          query], check=False).stdout.strip()
            row["pg_connections"] = int(output) if output.isdigit() else None
        if container_running("redis"):
            output = run(["docker", "exec", container("redis"), "sh", "-c",
                          "REDISCLI_AUTH=loadtest redis-cli dbsize"], check=False).stdout.strip()
            row["redis_keys"] = int(output) if output.isdigit() else None
        if tick % 10 == 0 and container_running("engine"):
            output = run(["docker", "exec", container("engine"), "sh", "-c",
                          "du -sm /var/lib/openagent/opencode /home/app/openagent-sessions 2>/dev/null"],
                         check=False).stdout.split()
            sizes = [int(item) for item in output if item.isdigit()]
            row["ws_mb"], row["state_mb"] = (sizes + [None, None])[:2]
        for name, url in (("engine", ENGINE_URL), ("router", ROUTER_URL)):
            status, text = http("GET", url + "/metrics", auth=False, timeout=8)
            if status == 200:
                metrics[name] = parse_metrics(text)
                row.update({f"{name}_{key}": value for key, value in flatten(metrics[name]).items()})
        return row, metrics


def cmd_sample(args):
    sampler = Sampler(args.out, interval=args.interval)
    sampler.start()
    try:
        if args.duration > 0:
            time.sleep(args.duration)
        else:
            while True:
                time.sleep(3600)
    except KeyboardInterrupt:
        pass
    finally:
        sampler.stop()


# ---------------------------------------------------------------- 场景执行

def collect_logs(out, start, end):
    for name in ("engine", "engine2", "router"):
        if not container_running(name):
            continue
        result = run(["docker", "logs", "--timestamps", "--since", start, "--until", end, container(name)], check=False,
                     timeout=120)
        text = result.stdout + result.stderr
        (out / f"{name}.log").write_text(text)
        timing = [line for line in text.splitlines() if "OpenCode turn timing" in line]
        (out / f"{name}-turn-timing.log").write_text("".join(line + "\n" for line in timing))
    events = run(["docker", "events", "--since", start, "--until", end, "--filter", "type=container",
                  "--filter", "event=oom", "--format", "{{.Time}} {{.Actor.Attributes.name}}"], check=False, timeout=60)
    (out / "oom-events.log").write_text(events.stdout)


def oom_kill_count():
    """内核日志里被 OOM 杀掉的进程数（累计值，运行前后各取一次求差）。"""
    result = run(["dmesg"], check=False)
    return sum(1 for line in result.stdout.splitlines() if "Killed process" in line)


def cmd_run(args):
    scenario_env = parse_kv(args.env)
    scenario_env.setdefault("TARGET", ROUTER_TARGET if args.via == "router" else ENGINE_TARGET)
    scenario_env.setdefault("SCENARIO", args.name)
    scenario_env.setdefault("LABEL", args.name)
    stub_overrides = parse_kv(args.stub)
    # 桩的配置是粘性的：每次都下发完整参数，避免上一次运行的故障注入残留到下一次。
    stub_config = {**STUB_DEFAULTS, **stub_overrides}
    if args.settle:
        print(f"==> settle {args.settle}s")
        time.sleep(args.settle)
    stub_payload_sent = stub_payload(stub_config) if stub_config else {}
    if stub_payload_sent:
        status, text = http("POST", STUB_URL + "/config", stub_payload_sent, auth=False)
        print(f"==> modelstub config: HTTP {status} {text}")
        if status != 200:
            raise SystemExit("failed to apply model stub config")
        verify_stub_config(stub_payload_sent, json.loads(text))
    http("POST", STUB_URL + "/stats/reset", body={}, auth=False)
    out = RESULTS / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{args.name}"
    out.mkdir(parents=True)
    scenario_env["OUT_DIR"] = f"/out/{out.name}"
    print(f"==> run {args.name} -> {out}")
    print("==> generator env:", json.dumps(scenario_env))
    sampler = Sampler(out, interval=args.interval)
    sampler.start()
    oom_before = oom_kill_count()
    started = now_iso()
    command = ["docker", "compose", "-f", COMPOSE, "-p", PROJECT, "run", "--rm", "-T"]
    for key, value in scenario_env.items():
        command += ["-e", f"{key}={value}"]
    command.append("generator")
    with open(out / "generator.log", "w") as log:
        process = subprocess.Popen(command, env=compose_env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, bufsize=1)
        for line in process.stdout:
            log.write(line)
            log.flush()
            if line.startswith("=="):
                print(line.rstrip())
        process.wait()
    # 时间戳只到秒，多留几秒，保证最后一个请求的收尾日志也被收进来。
    ended = now_iso(8)
    time.sleep(args.interval + 1)
    sampler.stop()
    collect_logs(out, started, ended)
    oom_after = oom_kill_count()
    _, text = http("GET", STUB_URL + "/stats", auth=False)
    (out / "modelstub-stats.json").write_text(text)
    images = run(["docker", "images", "--format", "{{.Repository}}:{{.Tag}} {{.ID}} {{.CreatedSince}}"], check=False).stdout
    engine_env = run(["docker", "inspect", "--format", "{{json .Config.Env}}", container("engine")]).stdout
    router_env = run(["docker", "inspect", "--format", "{{json .Config.Env}}", container("router")]).stdout
    memory_limit = run(["docker", "inspect", "--format", "{{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} "
                       "{{.HostConfig.NanoCpus}}", container("engine")]).stdout.split()
    manifest = {
        "name": args.name,
        "startedUtc": started,
        "endedUtc": ended,
        "generatorExitCode": process.returncode,
        "generatorEnv": scenario_env,
        "modelStubConfig": json.loads(http("GET", STUB_URL + "/stats", auth=False)[1])["config"],
        "modelStubOverrides": stub_overrides,
        "sampleIntervalSec": args.interval,
        "oomKillsDuringRun": oom_after - oom_before,
        "engineOomKilled": run(["docker", "inspect", "--format", "{{.State.OOMKilled}}", container("engine")],
                               check=False).stdout.strip() == "true",
        "engineLimits": {"memoryBytes": int(memory_limit[0]), "memorySwapBytes": int(memory_limit[1]),
                         "nanoCpus": int(memory_limit[2])} if len(memory_limit) == 3 else None,
        "gitHead": run(["git", "-C", REPO, "rev-parse", "HEAD"], check=False).stdout.strip(),
        "engineImage": container_image("engine"),
        "routerImage": container_image("router"),
        "images": [line for line in images.splitlines()
                   if line.startswith(("openagent-engine:", "openagent-router:", "openagent-loadtest-"))],
        "engineEnv": sorted(item for item in json.loads(engine_env)
                            if item.startswith(("OpenCode__", "Serilog__", "ASPNETCORE_ENVIRONMENT", "ConversationCache__"))),
        "routerEnv": sorted(item for item in json.loads(router_env)
                            if item.startswith(("RouterSettings__", "Serilog__", "ASPNETCORE_ENVIRONMENT"))),
    }
    (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
    print(f"==> generator exit {process.returncode}; OOM kills during run: {oom_after - oom_before}; results in {out}")
    print_report(out)


# ---------------------------------------------------------------- 评分卡

def container_image(name):
    if not container_running(name):
        return None
    return run(["docker", "inspect", "--format", "{{.Config.Image}} {{.Image}}", container(name)], check=False).stdout.strip()


def run_scenario(name, env, stub=None):
    """跑一个场景（复用 run 命令），返回其结果目录。"""
    args = argparse.Namespace(name=name, via="engine", env=env, stub=stub or [], interval=3.0, settle=2.0)
    with contextlib.redirect_stdout(io.StringIO()) as buffer:
        cmd_run(args)
    (RESULTS / f"{name}.log").write_text(buffer.getvalue(), encoding="utf-8")
    return max(RESULTS.glob(f"*-{name}"), key=lambda item: item.stat().st_mtime)


def scenario_metrics(out):
    analysis = analyze(out)
    manifest = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    phases = analysis.get("phases", {})
    main = phases.get("main") or max(phases.values(), key=lambda item: item["requests"])
    latency, timing = main["latency"], analysis.get("turnTiming", {})
    resources = analysis.get("resources", {})

    def latency_p50(name):
        return latency[name]["p50"]

    def timing_p50(name):
        return timing.get(name, {}).get("p50")

    def resource(name, key="max"):
        return resources.get(name, {}).get(key)

    return {
        "requests": main["requests"],
        "ok": main["ok"],
        "done_p50_ms": latency_p50("t_done_ms"),
        "done_p95_ms": latency["t_done_ms"]["p95"],
        "first_token_p50_ms": latency_p50("t_first_token_ms"),
        "runtime_p50_ms": latency_p50("t_runtime_ms"),
        "ok_per_sec": main["okPerSec"],
        "process_p50_ms": timing_p50("process"),
        "session_new_p50_ms": timing_p50("sessionNew"),
        "first_token_engine_p50_ms": timing_p50("firstToken"),
        "stream_p50_ms": timing_p50("stream"),
        "close_p50_ms": timing_p50("close"),
        "turn_total_p50_ms": timing_p50("total"),
        "engine_cpu_mean_pct": resource("engine_cpu", "mean"),
        "engine_cpu_max_pct": resource("engine_cpu"),
        "engine_mem_max_mb": resource("engine_mem_mb"),
        "opencode_count_max": resource("opencode_count"),
        "opencode_rss_max_mb": resource("opencode_rss_mb"),
        "oom_kills": manifest.get("oomKillsDuringRun"),
        "dir": out.name,
    }


def apply_engine(values, settle=2):
    args = argparse.Namespace(engine=values, router=[], multi=False, settle=settle)
    with contextlib.redirect_stdout(io.StringIO()):
        cmd_apply(args)


def opencode_ticks():
    result = run(["docker", "top", container("engine"), "-eo", "pid,args"], check=False)
    pids = [line.split()[0] for line in result.stdout.splitlines()[1:]
            if len(line.split()) > 1 and pathlib.PurePosixPath(line.split()[1]).name.startswith("opencode")]
    total = 0
    for pid in pids:
        try:
            with open(f"/proc/{pid}/stat") as file:
                fields = file.read().rsplit(")", 1)[1].split()
            total += int(fields[11]) + int(fields[12])
        except (OSError, IndexError, ValueError):
            continue
    return total, len(pids)


def sample_opencode_cpu(window=3.0):
    """OpenCode 子进程合计 CPU%（CLK_TCK=100）。"""
    start, _ = opencode_ticks()
    time.sleep(window)
    end, count = opencode_ticks()
    return round((end - start) / window, 1), count


def newest_sessions(count):
    result = run(["docker", "exec", container("engine"), "sh", "-c", f"ls -t {SESSION_ROOT} | head -{count}"], check=False)
    return result.stdout.split()


def session_dir_stats(entries):
    """已提交会话目录的体积（MB）与依赖安装残留（node_modules / .npm 目录数）。"""
    if not entries:
        return [], 0
    names = " ".join(entries)
    script = (f"cd {SESSION_ROOT} && du -sm {names} 2>/dev/null | cut -f1; "
              f"echo ---; find {names} -type d \\( -name node_modules -o -name .npm \\) ! -empty -prune -print 2>/dev/null | wc -l")
    lines = run(["docker", "exec", container("engine"), "sh", "-c", script], check=False).stdout.splitlines()
    sizes, junk, seen = [], 0, False
    for line in lines:
        if line.strip() == "---":
            seen = True
            continue
        if not line.strip().isdigit():
            continue
        if seen:
            junk = int(line)
        else:
            sizes.append(int(line))
    return sizes, junk


def measure_idle(label):
    """一次并发 8 轮制造 8 个保活进程，轮次结束后采样它们的 CPU；随后重建 Engine 让进程正常回收，再量目录体积。"""
    out = run_scenario(f"sc-{label}-idle8", ["MODEL=burst", "BURST_SIZE=8", "CONV_MODE=new"])
    entries = newest_sessions(8)
    samples = {}
    started = time.time()
    for target in (5, 15, 30, 45, 60):
        remaining = started + target - time.time()
        if remaining > 0:
            time.sleep(remaining)
        samples[f"cpu_pct_at_{target}s"], samples[f"processes_at_{target}s"] = sample_opencode_cpu()
    apply_engine(["LT_KEEPALIVE=0"], settle=2)
    sizes, junk = session_dir_stats(entries)
    metrics = scenario_metrics(out)
    metrics.update(samples)
    metrics["sessions_measured"] = len(sizes)
    metrics["session_mb_mean"] = round(statistics.fmean(sizes), 1) if sizes else None
    metrics["session_mb_max"] = max(sizes) if sizes else None
    metrics["dep_install_dirs"] = junk
    return metrics


def cmd_idle(args):
    """只跑空闲场景：一次 8 轮齐发制造 8 个保活进程，采样空闲 CPU，并在进程回收后量目录体积。"""
    SCORECARD.mkdir(parents=True, exist_ok=True)
    apply_engine(["LT_KEEPALIVE=300", "LT_MAX_KEPT=8"])
    metrics = measure_idle(args.label)
    (SCORECARD / f"{args.label}-idle.json").write_text(
        json.dumps({"label": args.label, "scenarios": {"idle8": metrics}, "engineImage": container_image("engine")},
                   ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


def load_scorecard(label):
    report = json.loads((SCORECARD / f"{label}.json").read_text(encoding="utf-8"))
    idle = SCORECARD / f"{label}-idle.json"
    if idle.exists():
        report["scenarios"]["idle8"] = json.loads(idle.read_text(encoding="utf-8"))["scenarios"]["idle8"]
    return report


def cmd_scorecard(args):
    """固定的一组场景，用来比较不同 Engine 镜像。所有场景直连 Engine、同一模型桩档位。"""
    stub = ["ttft_ms=300", "tokens=32", "interval_ms=25"]
    closed = ["MODEL=closed", "DURATION_SEC=3600"]
    SCORECARD.mkdir(parents=True, exist_ok=True)
    report = {"label": args.label, "startedUtc": now_iso(), "scenarios": {}}

    apply_engine(["LT_KEEPALIVE=0", "LT_MAX_KEPT=16", "LT_MAX_TURNS=8"])
    report["scenarios"]["cold1"] = scenario_metrics(run_scenario(
        f"sc-{args.label}-cold1", closed + ["CONV_MODE=new", "CONCURRENCY=1", "MAX_REQUESTS=40"], stub))
    report["engineImage"] = container_image("engine")

    apply_engine(["LT_KEEPALIVE=300", "LT_MAX_KEPT=8"])
    report["scenarios"]["warm1"] = scenario_metrics(run_scenario(
        f"sc-{args.label}-warm1", closed + ["CONV_MODE=worker", "CONCURRENCY=1", "MAX_REQUESTS=40"], stub))
    report["scenarios"]["warm_c4"] = scenario_metrics(run_scenario(
        f"sc-{args.label}-warm-c4", ["MODEL=closed", "DURATION_SEC=60", "CONV_MODE=worker", "CONCURRENCY=4"], stub))
    if not args.skip_idle:
        report["scenarios"]["idle8"] = measure_idle(args.label)
    else:
        apply_engine(["LT_KEEPALIVE=0"])

    report["scenarios"]["cold_c8"] = scenario_metrics(run_scenario(
        f"sc-{args.label}-cold-c8", ["MODEL=closed", "DURATION_SEC=60", "CONV_MODE=new", "CONCURRENCY=8"], stub))
    report["endedUtc"] = now_iso()
    (SCORECARD / f"{args.label}.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print_scorecard(report)


def shown(value):
    return f"{value:g}" if isinstance(value, (int, float)) else str(value)


def change_text(first, second, better):
    """变化幅度与方向；better 说明该指标越低越好（down）还是越高越好（up）。"""
    if first == second:
        return "持平"
    if not first:
        return "—"
    percent = (second - first) / abs(first) * 100
    improved = (percent < 0) == (better == "down")
    return f"{percent:+.1f}%（{'改善' if improved else '恶化'}）"


def cmd_compare(args):
    before = load_scorecard(args.before)
    after = load_scorecard(args.after)
    rows = [
        ("cold1", "新会话首轮 p50（毫秒）", "done_p50_ms", "down"),
        ("cold1", "新会话首轮 p95（毫秒）", "done_p95_ms", "down"),
        ("cold1", "首 token p50（毫秒）", "first_token_p50_ms", "down"),
        ("cold1", "拉起进程 p50（毫秒）", "process_p50_ms", "down"),
        ("cold1", "建会话 p50（毫秒）", "session_new_p50_ms", "down"),
        ("cold1", "收尾 p50（毫秒）", "close_p50_ms", "down"),
        ("cold1", "Engine 容器 CPU 平均（%）", "engine_cpu_mean_pct", "down"),
        ("warm1", "追问 p50（毫秒）", "done_p50_ms", "down"),
        ("warm1", "追问收尾 p50（毫秒）", "close_p50_ms", "down"),
        ("warm_c4", "追问 4 并发 p50（毫秒）", "done_p50_ms", "down"),
        ("warm_c4", "追问 4 并发吞吐（轮/秒）", "ok_per_sec", "up"),
        ("warm_c4", "OpenCode 进程峰值", "opencode_count_max", "down"),
        ("warm_c4", "OpenCode 内存峰值（MB）", "opencode_rss_max_mb", "down"),
        ("cold_c8", "新会话 8 并发 p50（毫秒）", "done_p50_ms", "down"),
        ("cold_c8", "新会话 8 并发吞吐（轮/秒）", "ok_per_sec", "up"),
        ("cold_c8", "OpenCode 进程峰值", "opencode_count_max", "down"),
        ("cold_c8", "OpenCode 内存峰值（MB）", "opencode_rss_max_mb", "down"),
        ("cold_c8", "OOM 杀进程次数", "oom_kills", "down"),
        ("idle8", "空闲 15 秒时 CPU（%）", "cpu_pct_at_15s", "down"),
        ("idle8", "空闲 30 秒时 CPU（%）", "cpu_pct_at_30s", "down"),
        ("idle8", "空闲 60 秒时 CPU（%）", "cpu_pct_at_60s", "down"),
        ("idle8", "进程回收后每会话落盘（MB）", "session_mb_mean", "down"),
        ("idle8", "非空的依赖安装目录数", "dep_install_dirs", "down"),
    ]
    print(f"\n===== {args.before} → {args.after} =====")
    print(f"{'场景':<10} {'指标':<30} {args.before[:18]:>18} {args.after[:18]:>18} {'变化':>10}")
    for scenario, title, key, better in rows:
        first = before["scenarios"].get(scenario, {}).get(key)
        second = after["scenarios"].get(scenario, {}).get(key)
        if first is None or second is None:
            continue
        print(f"{scenario:<10} {title:<30} {shown(first):>18} {shown(second):>18} {change_text(first, second, better):>12}")
    print(f"镜像：{before.get('engineImage')} → {after.get('engineImage')}")
    markdown = [f"### {args.before} → {args.after}", "",
                "| 场景 | 指标 | 修改前 | 修改后 | 变化 |", "|---|---|---:|---:|---|"]
    for scenario, title, key, better in rows:
        first = before["scenarios"].get(scenario, {}).get(key)
        second = after["scenarios"].get(scenario, {}).get(key)
        if first is None or second is None:
            continue
        markdown.append(f"| {scenario} | {title} | {shown(first)} | {shown(second)} | {change_text(first, second, better)} |")
    (SCORECARD / f"compare-{args.before}-{args.after}.md").write_text("\n".join(markdown) + "\n", encoding="utf-8")


def print_scorecard(report):
    print(f"\n===== scorecard {report['label']} ({report.get('engineImage')}) =====")
    for name, metrics in report["scenarios"].items():
        print(f"--- {name}")
        for key, value in metrics.items():
            if key != "dir":
                print(f"  {key:<28} {value}")


# ---------------------------------------------------------------- 结果汇总

def load_rows(path):
    with open(path, newline="", encoding="utf-8") as file:
        return list(csv.DictReader(file))


def column(rows, name):
    values = []
    for row in rows:
        if row.get(name):
            try:
                values.append(float(row[name]))
            except ValueError:
                pass
    return values


def analyze(out):
    out = pathlib.Path(out)
    requests = sorted(out.glob("requests-*.csv"))
    summaries = sorted(out.glob("summary-*.json"))
    analysis = {"dir": out.name}
    if not requests:
        return analysis
    rows = load_rows(requests[0])
    if summaries:
        analysis["summary"] = json.loads(summaries[0].read_text())
    phases = {}
    for row in rows:
        phases.setdefault(row["phase"], []).append(row)
    latency_columns = ("t_headers_ms", "t_conversation_ms", "t_runtime_ms", "t_first_token_ms", "t_done_ms")
    phase_stats = {}
    for phase, items in phases.items():
        ok = [item for item in items if item["outcome"] == "ok"]
        outcomes = {}
        for item in items:
            outcomes[item["outcome"]] = outcomes.get(item["outcome"], 0) + 1
        status_codes = {}
        for item in items:
            status_codes[item["status_code"]] = status_codes.get(item["status_code"], 0) + 1
        starts = column(items, "start_ms")
        ends = [float(item["start_ms"]) + float(item["t_done_ms"]) for item in items if item["t_done_ms"]]
        window = (max(ends) - min(starts)) / 1000 if starts and ends else 0
        stats = {
            "requests": len(items),
            "ok": len(ok),
            "outcomes": outcomes,
            "statusCodes": status_codes,
            "windowSec": round(window, 1),
            "okPerSec": round(len(ok) / window, 2) if window > 0 else None,
            "latency": {},
        }
        for name in latency_columns:
            values = column(ok, name)
            stats["latency"][name] = {
                "n": len(values),
                "mean": round(statistics.fmean(values), 1) if values else None,
                "p50": round(percentile(values, 0.50), 1) if values else None,
                "p90": round(percentile(values, 0.90), 1) if values else None,
                "p95": round(percentile(values, 0.95), 1) if values else None,
                "p99": round(percentile(values, 0.99), 1) if values else None,
                "max": round(max(values), 1) if values else None,
            }
        phase_stats[phase] = stats
    analysis["phases"] = phase_stats
    timing_path = out / "engine-turn-timing.log"
    if timing_path.exists():
        fields = ("process", "sessionNew", "prompt", "firstUpdate", "firstToken", "stream", "close", "total")
        values = {field: [] for field in fields}
        for line in timing_path.read_text().splitlines():
            match = TURN_TIMING.search(line)
            if match:
                for field, value in zip(fields, match.groups()):
                    values[field].append(int(value))
        analysis["turnTiming"] = {field: {
            "n": len(items),
            "mean": round(statistics.fmean(items), 1) if items else None,
            "p50": round(percentile(items, 0.50), 1) if items else None,
            "p95": round(percentile(items, 0.95), 1) if items else None,
            "max": max(items) if items else None,
        } for field, items in values.items()}
        if values["process"]:
            analysis["turnTiming"]["coldCount"] = sum(1 for item in values["process"] if item >= 200)
    resources_path = out / "resources.ndjson"
    if resources_path.exists():
        samples = [json.loads(line) for line in resources_path.read_text().splitlines() if line.strip()]
        resource_stats = {}
        for key in RESOURCE_KEYS:
            values = [sample[key] for sample in samples if isinstance(sample.get(key), (int, float))]
            if not values:
                continue
            resource_stats[key] = {
                "mean": round(statistics.fmean(values), 1),
                "max": round(max(values), 1),
                "maxAt": next((sample["ts"] for sample in samples if sample.get(key) == max(values)), None),
                "last": values[-1],
            }
        analysis["resources"] = resource_stats
        analysis["samples"] = len(samples)
    return analysis


def print_report(out):
    analysis = analyze(out)
    summary = analysis.get("summary")
    if not summary:
        print(f"report: no requests file in {out}")
        return analysis
    print(f"\n===== {analysis['dir']} =====")
    print(f"started {summary['startedUtc']}  duration {summary['durationSec']}s  completed {summary['completed']}"
          f"  overload-skipped {summary['overloadSkipped']}")
    print("outcomes:", json.dumps(summary["outcomes"]), " status codes:", json.dumps(summary["statusCodes"]),
          " done statuses:", json.dumps(summary["doneStatuses"]))
    if summary["details"]:
        print("top details:", json.dumps(summary["details"], ensure_ascii=False))
    print(f"\n{'phase':<10} {'n':>6} {'ok':>6} {'ok/s':>7}  {'conversation p50/p95/p99':<26} {'runtime p50/p95/p99':<24}"
          f" {'first token p50/p95/p99':<26} {'done p50/p95/p99':<24} outcomes")
    for phase, stats in analysis["phases"].items():
        cells = ["/".join(fmt(stats["latency"][name][key]) for key in ("p50", "p95", "p99"))
                 for name in ("t_conversation_ms", "t_runtime_ms", "t_first_token_ms", "t_done_ms")]
        print(f"{phase:<10} {stats['requests']:>6} {stats['ok']:>6} {fmt(stats['okPerSec'], 2):>7}  "
              + f" {cells[0]:<26} {cells[1]:<24} {cells[2]:<26} {cells[3]:<24} {json.dumps(stats['outcomes'])}")
    if "turnTiming" in analysis:
        timing = analysis["turnTiming"]
        print("\nEngine turn timing (ms), from EventId 4501 logs:")
        print(f"{'field':<12} {'n':>6} {'mean':>8} {'p50':>8} {'p95':>8} {'max':>8}")
        for field in ("process", "sessionNew", "prompt", "firstUpdate", "firstToken", "stream", "close", "total"):
            item = timing[field]
            print(f"{field:<12} {item['n']:>6} {fmt(item['mean']):>8} {fmt(item['p50']):>8} {fmt(item['p95']):>8}"
                  f" {fmt(item['max']):>8}")
        print(f"cold starts (process >= 200 ms): {timing.get('coldCount', 0)}")
    if "resources" in analysis:
        print("\nResources (mean / max):")
        for key, item in analysis["resources"].items():
            print(f"  {key:<22} {fmt(item['mean'], 1):>10} / {fmt(item['max'], 1):>8}")
    return analysis


def cmd_report(args):
    for out in args.dirs:
        analysis = print_report(out)
        (pathlib.Path(out) / "analysis.json").write_text(json.dumps(analysis, ensure_ascii=False, indent=2))


# ---------------------------------------------------------------- 入口

def add_stub_arguments(parser):
    parser.add_argument("--reset", action="store_true")
    parser.add_argument("--ttft-ms", dest="ttft_ms", type=int)
    parser.add_argument("--tokens", type=int)
    parser.add_argument("--interval-ms", dest="interval_ms", type=int)
    parser.add_argument("--error-rate", dest="error_rate", type=float)
    parser.add_argument("--error-status", dest="error_status", type=int)
    parser.add_argument("--hang-rate", dest="hang_rate", type=float)
    parser.add_argument("--slow-rate", dest="slow_rate", type=float)
    parser.add_argument("--slow-ttft-ms", dest="slow_ttft_ms", type=int)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    stack_up = commands.add_parser("stack-up")
    stack_up.add_argument("--build", action="store_true")
    stack_up.add_argument("--multi", action="store_true")
    stack_up.set_defaults(func=cmd_stack_up)

    stack_down = commands.add_parser("stack-down")
    stack_down.add_argument("-v", action="store_true", dest="volumes")
    stack_down.set_defaults(func=cmd_stack_down)

    commands.add_parser("status").set_defaults(func=cmd_status)

    scorecard = commands.add_parser("scorecard")
    scorecard.add_argument("label")
    scorecard.add_argument("--skip-idle", action="store_true")
    scorecard.set_defaults(func=cmd_scorecard)

    idle = commands.add_parser("idle")
    idle.add_argument("label")
    idle.set_defaults(func=cmd_idle)

    compare_parser = commands.add_parser("compare")
    compare_parser.add_argument("before")
    compare_parser.add_argument("after")
    compare_parser.set_defaults(func=cmd_compare)

    apply_parser = commands.add_parser("apply")
    apply_parser.add_argument("--engine", nargs="*")
    apply_parser.add_argument("--router", nargs="*")
    apply_parser.add_argument("--multi", action="store_true")
    apply_parser.add_argument("--settle", type=float, default=8)
    apply_parser.set_defaults(func=cmd_apply)

    seed = commands.add_parser("seed")
    seed.add_argument("--smoke", action="store_true")
    seed.add_argument("--via", default="engine")
    seed.set_defaults(func=cmd_seed)

    seed_data = commands.add_parser("seed-data")
    seed_data.add_argument("--count", type=int, default=10000)
    seed_data.add_argument("--long-count", dest="long_count", type=int, default=5)
    seed_data.add_argument("--long-turns", dest="long_turns", type=int, default=200)
    seed_data.set_defaults(func=cmd_seed_data)

    stub = commands.add_parser("stub")
    add_stub_arguments(stub)
    stub.set_defaults(func=cmd_stub)

    run_parser = commands.add_parser("run")
    run_parser.add_argument("name")
    run_parser.add_argument("--via", default="engine")
    run_parser.add_argument("--env", action="append", default=[])
    run_parser.add_argument("--stub", action="append", default=[])
    run_parser.add_argument("--interval", type=float, default=3.0)
    run_parser.add_argument("--settle", type=float, default=5)
    run_parser.set_defaults(func=cmd_run)

    sample = commands.add_parser("sample")
    sample.add_argument("--out", required=True)
    sample.add_argument("--duration", type=float, default=0)
    sample.add_argument("--interval", type=float, default=3.0)
    sample.set_defaults(func=cmd_sample)

    report = commands.add_parser("report")
    report.add_argument("dirs", nargs="*")
    report.set_defaults(func=cmd_report)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
