#!/usr/bin/env python3
"""Bounded real-OpenCode/local-model comparison; never enables a host setting.

Requires Python 3, dotnet, ps and optionally lsof (macOS/Linux).
Build Runtime.Tests first. Exit 0 means the probe completed, NOT enablement approval.
"""
import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import statistics
import math
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Backend/tests/OpenAgent.Runtime.Tests/OpenAgent.Runtime.Tests.csproj'


def cpu_seconds(value):
    days, _, value = value.rpartition('-') if '-' in value else ('0', '', value)
    parts = [float(part) for part in value.split(':')]
    return int(days) * 86400 + sum(part * 60 ** index for index, part in enumerate(reversed(parts)))


def processes():
    result = subprocess.run(['ps', '-axo', 'pid=,ppid=,time=,rss=,comm='],
                            capture_output=True, text=True, check=True, timeout=5)
    rows = {}
    for line in result.stdout.splitlines():
        parts = line.strip().split(None, 4)
        if len(parts) == 5:
            pid, parent, cpu, rss, name = parts
            used = cpu_seconds(cpu)
            if platform.system() == 'Linux':
                try:
                    fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
                    used = (int(fields[11]) + int(fields[12])) / os.sysconf('SC_CLK_TCK')
                except (OSError, ValueError, IndexError):
                    pass  # Process may have exited since ps; never inspect command lines/environment.
            rows[int(pid)] = (int(parent), used, int(rss), name)
    return rows


def descendants(rows, root):
    found = {root}
    while True:
        more = {pid for pid, row in rows.items() if row[0] in found}
        if more <= found:
            return found
        found |= more


def sockets(pids):
    lsof = shutil.which('lsof') or ('/usr/sbin/lsof' if Path('/usr/sbin/lsof').exists() else None)
    if not lsof or not pids:
        return {'ObservedSockets': None, 'NonLoopbackPeers': None}
    result = subprocess.run([lsof, '-nP', '-a', '-p', ','.join(map(str, pids)), '-i', '-F', 'pn'],
                            capture_output=True, text=True, timeout=5)
    if result.returncode not in (0, 1):
        return {'ObservedSockets': None, 'NonLoopbackPeers': None}
    names = [line[1:] for line in result.stdout.splitlines() if line.startswith('n')]
    external = 0
    for name in names:
        if '->' in name:
            address = name.split('->', 1)[1].rsplit(':', 1)[0].strip('[]')
            try:
                external += not ipaddress.ip_address(address).is_loopback
            except ValueError:
                external += 1
    return {'ObservedSockets': len(names), 'NonLoopbackPeers': external}


def stop_group(child):
    # Only the new process group created for this probe; never match machine-wide names.
    try:
        os.killpg(child.pid, signal.SIGINT)
    except ProcessLookupError:
        return
    try:
        child.wait(timeout=8)
    except subprocess.TimeoutExpired:
        pass
    # The group may contain children even after dotnet itself has exited.
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


def run_case(args, keep, owners):
    output = args.output / f'keep-{keep}-owners-{owners}.json'
    phase = Path(str(output) + '.phase')
    for path in (output, phase):
        path.unlink(missing_ok=True)
    env = dict(os.environ, OPENAGENT_TEST_OPENCODE=str(args.executable),
               OPENAGENT_KEEPALIVE_PROBE=str(output), N04_KEEP_SECONDS=str(keep), N04_OWNERS=str(owners))
    samples, previous, tracked = [], {}, set()
    previous_host = {}
    abort = None
    start = time.monotonic()
    previous_time = start
    with output.with_suffix('.log').open('w') as log:
        child = subprocess.Popen(['dotnet', 'test', str(PROJECT), '--no-build', '--nologo',
                                  '--filter', 'FullyQualifiedName~OpenCodeKeepAliveProbe'],
                                 cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while child.poll() is None:
                time.sleep(0.5)
                rows = processes()
                tree = descendants(rows, child.pid)
                harness = {pid for pid in tree if pid in rows and Path(rows[pid][3]).name.startswith('opencode')}
                tracked |= harness
                now = time.monotonic()
                duration = now - previous_time
                # CPU percentage of one logical core; 100 means one fully occupied core.
                cpu = sum(max(0, rows[pid][1] - previous[pid]) for pid in harness if pid in previous) / duration * 100
                group_cpu = sum(max(0, row[1] - previous[pid]) for pid, row in rows.items() if pid in tree and pid in previous) / duration * 100
                rss = sum(rows[pid][2] for pid in harness) / 1024
                sample = {'ElapsedSeconds': round(now - start, 3),
                          'Phase': phase.read_text().strip() if phase.exists() else 'turns',
                          'Count': len(harness), 'CpuPercentOneCore': round(cpu, 2),
                          'TreeCpuPercentOneCore': round(group_cpu, 2),
                          'RssMiB': round(rss, 2),
                          'MaxProcessRssMiB': round(max((rows[pid][2] for pid in harness), default=0) / 1024, 2),
                          'HostCpuPercentOneCore': round(sum(max(0, row[1] - previous_host[pid]) for pid, row in rows.items() if pid in previous_host) / duration * 100, 2),
                          'HostLoadAverage': list(os.getloadavg()),
                          'TreeRssMiB': round(sum(rows[pid][2] for pid in tree if pid in rows) / 1024, 2)}
                if sample['Phase'] == 'idle':
                    sample.update(sockets(harness))
                samples.append(sample)
                previous = {pid: row[1] for pid, row in rows.items() if pid in tree}
                previous_host = {pid: row[1] for pid, row in rows.items()}
                previous_time = now
                if rss > 1536 or len(harness) > 2 or (sample['Phase'] == 'idle' and cpu > 300):
                    abort = 'hard resource ceiling (RSS 1536 MiB / 2 harnesses / idle 3 cores)'
                if now - start > 120:
                    abort = '120 second case deadline'
                if abort:
                    break
        finally:
            stop_group(child)
    result = json.loads(output.read_text()) if output.exists() else {}
    idle = [sample for sample in samples if sample['Phase'] == 'idle']
    remaining = tracked & processes().keys()
    warm = [turn for turn in result.get('Turns', []) if turn['Turn'] > 0]
    def latency(field):
        values = sorted(turn[field] for turn in warm if turn[field] is not None)
        return {'Samples': len(values), 'P50Ms': statistics.median(values),
                'P95Ms': values[math.ceil(len(values) * .95) - 1]} if values else None
    population_ok = bool(idle) and all(s['Count'] == (owners if keep > 0 else 0) for s in idle)
    result.update(IdlePopulationVerified=population_ok, WarmTtft=latency('TtftMs'), WarmTotal=latency('TotalMs'), Samples=samples, Aborted=abort, TestExitCode=child.returncode,
                  SurvivingHarnessCount=len(remaining),
                  IdleCpuMeanPercentOneCore=round(sum(s['CpuPercentOneCore'] for s in idle) / len(idle), 2) if idle else None,
                  IdlePeakRssMiB=max((s['RssMiB'] for s in idle), default=None),
                  IdlePeakProcessRssMiB=max((s['MaxProcessRssMiB'] for s in idle), default=None),
                  NetworkEvidence='socket snapshots and local model request count; packet/DNS/failed-attempt coverage unavailable',
                  NetworkGate='failed' if result.get('IdleModelRequests', 0) > 0 or any(s.get('NonLoopbackPeers', 0) or 0 for s in idle) else 'unverified',
                  ResourceGate=population_ok and not abort and all(s['RssMiB'] <= owners * 512 for s in idle)
                  and all(s['MaxProcessRssMiB'] <= 512 for s in idle)
                  and sum(s['CpuPercentOneCore'] for s in idle) / len(idle) <= owners * 5)
    output.write_text(json.dumps(result, indent=2) + '\n')
    phase.unlink(missing_ok=True)
    print(json.dumps({key: result.get(key) for key in ('KeepAliveSeconds', 'Owners', 'Hits', 'ResourceGate',
                     'IdleCpuMeanPercentOneCore', 'IdlePeakRssMiB', 'Aborted', 'SurvivingHarnessCount')}), flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--owners', type=int, choices=(1, 2), default=1,
                        help='Run 1 first; use 2 only if the 1-owner resource gate passes.')
    args = parser.parse_args()
    if platform.system() not in ('Darwin', 'Linux'):
        parser.error('Only macOS/Linux ps sampling is supported; Windows needs an equivalent counter/egress probe.')
    args.executable = args.executable.resolve(strict=True)
    args.output = args.output.resolve()
    version = subprocess.check_output([str(args.executable), '--version'], text=True, timeout=15).strip()
    pinned = (ROOT / 'Backend/opencode.version').read_text().strip()
    if version != pinned:
        parser.error(f'executable version {version} differs from pinned version {pinned}')
    args.output.mkdir(parents=True, exist_ok=True)
    revision = os.environ.get('N04_SOURCE_COMMIT')
    if not revision:
        git = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True, capture_output=True)
        if git.returncode:
            parser.error('Set N04_SOURCE_COMMIT when running an exported/container source tree.')
        revision = git.stdout.strip()
    source = hashlib.sha256()
    for directory in ('src/OpenAgent.Runtime', 'src/OpenAgent.Runtime.Acp', 'src/OpenAgent.Contracts',
                      'tests/OpenAgent.Runtime.Tests'):
        for path in sorted((ROOT / 'Backend' / directory).rglob('*')):
            if path.is_file() and path.suffix in ('.cs', '.csproj') and not {'bin', 'obj'} & set(path.parts):
                source.update(str(path.relative_to(ROOT)).encode() + b'\0' + path.read_bytes())
    metadata = {'Version': version, 'ExecutableSha256': hashlib.sha256(args.executable.read_bytes()).hexdigest(),
                'Platform': platform.platform(), 'LogicalCpus': os.cpu_count(),
                'Commit': revision, 'RuntimeSourceSha256': source.hexdigest(),
                'Thresholds': {'IdleCpuPercentOneCorePerProcess': 5, 'RssMiBPerProcess': 512,
                               'UnexplainedIdleEgress': 0, 'WarmTtftP50ImprovementPercent': 20},
                'Conclusion': 'Defaults preserved at 300 seconds / 16 idle owners by user decision; production approval still requires isolation, capacity and egress gates.'}
    results = [run_case(args, keep, 1) for keep in (0, 30)]
    metadata['TwoOwnerCase'] = 'not requested'
    if args.owners == 2:
        if results[-1]['ResourceGate']:
            metadata['TwoOwnerCase'] = 'executed after one-owner resource gate'
            results.extend(run_case(args, keep, 2) for keep in (0, 30))
        else:
            metadata['TwoOwnerCase'] = 'stopped: one-owner resource gate failed'
    metadata['ProbeCompleted'] = all(not r['Aborted'] and r['TestExitCode'] == 0 and r.get('CleanupVerified')
                                     and not r['SurvivingHarnessCount'] for r in results)
    (args.output / 'manifest.json').write_text(json.dumps(metadata, indent=2) + '\n')
    return 0 if metadata['ProbeCompleted'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
