#!/usr/bin/env python3
"""把 artifacts/loadtest/results 下所有已完成的运行汇总成一份 Markdown / JSON 总表（离线执行，不接触被测栈）。

  python3 tools/loadtest/scripts/summarize.py [RESULTS_DIR]
"""

import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import lt  # noqa: E402

RESULTS = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else lt.RESULTS


def describe(manifest):
    env = dict(item.split("=", 1) for item in manifest.get("engineEnv", []) if item.startswith("OpenCode__"))
    generator = manifest.get("generatorEnv", {})
    parts = [
        f"并发上限 {env.get('OpenCode__MaxConcurrentTurns', '?')}",
        f"空闲上限 {env.get('OpenCode__MaxKeptProcesses', '?')}",
        f"保活 {env.get('OpenCode__KeepAliveSeconds', '?')}s",
    ]
    target = "经 Router" if "router" in generator.get("TARGET", "") else "直连 Engine"
    if generator.get("MODEL") == "ramp":
        load = "爬坡 " + generator.get("RAMP", "").replace(",", " → ")
    elif generator.get("MODEL") == "open":
        load = f"到达率 {generator.get('RATE', '?')}/s"
    elif generator.get("MODEL") == "burst":
        load = f"齐发 {generator.get('BURST_SIZE', '?')} × {generator.get('BURST_ROUNDS', '1')}"
    else:
        load = f"并发 {generator.get('CONCURRENCY', '?')}"
    stub = manifest.get("modelStubConfig") or {}
    return "；".join(parts), f"{target}；{load}", stub


def triple(latency):
    return "/".join(lt.fmt(latency[key]) for key in ("p50", "p95", "p99")) if latency and latency.get("n") else "-"


def main():
    runs = []
    for manifest_path in sorted(RESULTS.glob("*/manifest.json")):
        out = manifest_path.parent
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        analysis = lt.analyze(out)
        if "phases" not in analysis:
            continue
        analysis["manifest"] = manifest
        (out / "analysis.json").write_text(json.dumps(analysis, ensure_ascii=False, indent=2), encoding="utf-8")
        runs.append(analysis)

    lines = ["# 压测结果总表", "",
             "时延单位为毫秒，格式 p50/p95/p99，只统计成功的请求；`ok/s` 为该阶段成功轮次除以阶段时长。", "",
             "| 运行 | 被测配置 | 负载 | 模型桩 (首包ms/分片数/间隔ms) | 时长s | 请求 | 成功 | 非成功 | 首 token | done | ok/s |"
             " Engine CPU 峰值% | 容器内存峰值MB | OpenCode 进程峰值 | OOM 杀进程 |",
             "|---|---|---|---|---:|---:|---:|---|---|---|---:|---:|---:|---:|---:|"]
    phase_lines = ["", "## 分阶段时延", "",
                   "| 运行 | 阶段 | 请求 | 成功 | 非成功 | conversation | runtime | 首 token | done | ok/s |",
                   "|---|---|---:|---:|---|---|---|---|---|---:|"]
    timing_lines = ["", "## Engine 轮次分段计时（EventId 4501，p50 毫秒）", "",
                    "| 运行 | n | process | sessionNew | firstToken | stream | close | total | 冷启动数 |",
                    "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    resource_keys = ("engine_cpu", "engine_mem_mb", "opencode_count", "opencode_rss_mb", "engine_rss_mb",
                     "generator_cpu", "pg_connections", "host_mem_avail_mb", "state_mb", "ws_mb")
    resource_lines = ["", "## 资源（平均 / 峰值；host_mem_avail_mb 为最低可用内存）", "",
                      "| 运行 | " + " | ".join(resource_keys) + " |", "|---|" + "---|" * len(resource_keys)]
    for analysis in runs:
        manifest = analysis["manifest"]
        config, load, stub = describe(manifest)
        summary = analysis["summary"]
        phases = analysis["phases"]
        total_requests = sum(item["requests"] for item in phases.values())
        total_ok = sum(item["ok"] for item in phases.values())
        failures = {}
        for item in phases.values():
            for outcome, count in item["outcomes"].items():
                if outcome != "ok":
                    failures[outcome] = failures.get(outcome, 0) + count
        main_phase = phases.get("main") or max(phases.values(), key=lambda item: item["requests"])
        resources = analysis.get("resources", {})
        peak = lambda key: lt.fmt(resources.get(key, {}).get("max"), 0)  # noqa: E731
        stub_text = f"{stub.get('ttft_ms', 300)}/{stub.get('tokens', 32)}/{stub.get('interval_ms', 25)}"
        lines.append(
            f"| {analysis['dir'].split('-', 1)[1]} | {config} | {load} | {stub_text} | {summary['durationSec']} | "
            f"{total_requests} | {total_ok} | {json.dumps(failures) if failures else '无'} | "
            f"{triple(main_phase['latency']['t_first_token_ms'])} | {triple(main_phase['latency']['t_done_ms'])} | "
            f"{lt.fmt(main_phase['okPerSec'], 2)} | {peak('engine_cpu')} | {peak('engine_mem_mb')} | "
            f"{peak('opencode_count')}（{peak('opencode_rss_mb')} MB） | {manifest.get('oomKillsDuringRun', '?')} |")
        for phase, item in phases.items():
            phase_lines.append(
                f"| {analysis['dir'].split('-', 1)[1]} | {phase} | {item['requests']} | {item['ok']} | "
                f"{json.dumps({k: v for k, v in item['outcomes'].items() if k != 'ok'})} | "
                f"{triple(item['latency']['t_conversation_ms'])} | {triple(item['latency']['t_runtime_ms'])} | "
                f"{triple(item['latency']['t_first_token_ms'])} | {triple(item['latency']['t_done_ms'])} | "
                f"{lt.fmt(item['okPerSec'], 2)} |")
        timing = analysis.get("turnTiming")
        if timing:
            timing_lines.append(
                f"| {analysis['dir'].split('-', 1)[1]} | {timing['process']['n']} | "
                + " | ".join(lt.fmt(timing[field]["p50"]) for field in
                            ("process", "sessionNew", "firstToken", "stream", "close", "total"))
                + f" | {timing.get('coldCount', 0)} |")
        if resources:
            cells = [f"{lt.fmt(resources[key]['mean'])} / {lt.fmt(resources[key]['max'])}" if key in resources else "-"
                     for key in resource_keys]
            avail = resources.get("host_mem_avail_mb")
            cells[resource_keys.index("host_mem_avail_mb")] = (
                f"最低 {lt.fmt(min(sample.get('host_mem_avail_mb', 0) for sample in samples_of(analysis)))}"
                if avail else "-")
            resource_lines.append(f"| {analysis['dir'].split('-', 1)[1]} | " + " | ".join(cells) + " |")
    (RESULTS / "SUMMARY.md").write_text("\n".join(lines + phase_lines + timing_lines + resource_lines) + "\n",
                                        encoding="utf-8")
    (RESULTS / "summary.json").write_text(json.dumps(runs, ensure_ascii=False, indent=1), encoding="utf-8")
    print("\n".join(lines + phase_lines + timing_lines + resource_lines))


def samples_of(analysis):
    path = RESULTS / analysis["dir"] / "resources.ndjson"
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


if __name__ == "__main__":
    main()
