#!/usr/bin/env python3
"""Start/stop a loopback-only OpenAgent preview without Docker or external stores."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
STATE = ROOT / '.preview' / 'local'


def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def stop():
    state_file = STATE / 'processes.json'
    if not state_file.exists():
        return
    for item in json.loads(state_file.read_text()):
        if alive(item['pid']):
            os.killpg(item['pid'], signal.SIGTERM)
    state_file.unlink()
    print('Local preview stopped. In-memory preview data is discarded.')


def start(args):
    STATE.mkdir(parents=True, exist_ok=True)
    state_file = STATE / 'processes.json'
    if state_file.exists() and any(alive(item['pid']) for item in json.loads(state_file.read_text())):
        raise SystemExit('Preview is already running; run down before restarting.')
    executable = shutil.which(args.opencode)
    if not executable:
        raise SystemExit('OpenCode was not found. Install OpenCode 1.18.35 or supply --opencode /absolute/path.')
    for command in ('dotnet', 'pnpm'):
        if not shutil.which(command):
            raise SystemExit(f'{command} is required.')
    for port in (args.engine_port, args.web_port):
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(('127.0.0.1', port))
    subprocess.run(['dotnet', 'build', 'Backend/OpenAgent.sln'], cwd=ROOT, check=True)
    frontend = ROOT / 'Frontend/OpenAgent.Chat'
    subprocess.run(['pnpm', 'install', '--frozen-lockfile'], cwd=frontend, check=True)
    backend_url = f'http://127.0.0.1:{args.engine_port}'
    web_url = f'http://127.0.0.1:{args.web_port}'
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=backend_url,
               OpenCode__Executable=executable, Cors__AllowedOrigins__0=web_url,
               ConnectionStrings__Redis='', LocalDevelopment__UseInMemoryStores='true',
               VITE_OPENAGENT_CONNECTION_MODE='engine',
               VITE_OPENAGENT_ROUTER_BASE_URL=backend_url, VITE_OPENAGENT_ENGINE_BASE_URL=backend_url)
    processes = []
    try:
        for name, command, cwd in (
            ('engine', ['dotnet', 'run', '--no-build', '--no-launch-profile', '--project', 'Backend/src/OpenAgent.Engine.Host'], ROOT),
            ('web', ['pnpm', 'dev', '--host', '127.0.0.1', '--port', str(args.web_port), '--strictPort'], frontend),
        ):
            with (STATE / f'{name}.log').open('w') as log:
                process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                           stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            processes.append({'name': name, 'pid': process.pid})
            state_file.write_text(json.dumps(processes))
        for url in (backend_url + '/health/report', web_url):
            deadline = time.monotonic() + 45
            while True:
                try:
                    with urllib.request.urlopen(url, timeout=2) as response:
                        if response.status == 200:
                            break
                except OSError:
                    pass
                if time.monotonic() > deadline or not all(alive(item['pid']) for item in processes):
                    raise RuntimeError(f'Preview startup failed. Check {STATE} for logs.')
                time.sleep(0.5)
    except BaseException:
        stop()
        raise
    print(f'Preview: {web_url}\nEngine: {backend_url}\nLogs: {STATE}')
    print('Development preview only: data is kept in memory and lost on restart. Configure model connections in Settings.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['up', 'down', 'status'])
    parser.add_argument('--engine-port', type=int, default=5240)
    parser.add_argument('--web-port', type=int, default=5184)
    parser.add_argument('--opencode', default='opencode')
    args = parser.parse_args()
    if args.action == 'up':
        start(args)
    elif args.action == 'down':
        stop()
    else:
        state_file = STATE / 'processes.json'
        for item in json.loads(state_file.read_text()) if state_file.exists() else []:
            print(item['name'], 'running' if alive(item['pid']) else 'stopped')
