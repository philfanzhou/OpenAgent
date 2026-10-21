#!/usr/bin/env python3
"""Start/stop a loopback-only OpenAgent preview with native PostgreSQL and durable local files, without Docker."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import secrets
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
    print('Local preview stopped. PostgreSQL and local files are retained.')


def database(args):
    data_root = Path(args.data_root).expanduser().resolve()
    data_root.mkdir(parents=True, exist_ok=True)
    if args.database:
        return args.database, data_root
    pg_bin = Path(args.postgres_bin) if args.postgres_bin else Path(shutil.which('pg_ctl') or '/opt/homebrew/opt/postgresql@17/bin/pg_ctl').parent
    if not (pg_bin / 'pg_ctl').exists():
        raise SystemExit('Native PostgreSQL is required. Install PostgreSQL or supply --postgres-bin /path/to/bin or --database CONNECTION_STRING.')
    password_file = data_root / 'postgres-password'
    if not password_file.exists():
        password_file.write_text(secrets.token_hex(24))
        password_file.chmod(0o600)
    password = password_file.read_text().strip()
    pg_data = data_root / 'postgres'
    if not (pg_data / 'PG_VERSION').exists():
        subprocess.run([str(pg_bin / 'initdb'), '-D', str(pg_data), '-U', 'openagent', '--encoding=UTF8', '--locale=C', '--auth=scram-sha-256', '--pwfile', str(password_file)], check=True)
    running = subprocess.run([str(pg_bin / 'pg_ctl'), '-D', str(pg_data), 'status'], stdout=subprocess.DEVNULL).returncode == 0
    if not running:
        subprocess.run([str(pg_bin / 'pg_ctl'), '-D', str(pg_data), '-l', str(data_root / 'postgres.log'), '-o', f'-h 127.0.0.1 -p {args.postgres_port}', '-w', 'start'], check=True)
    pg_env = dict(os.environ, PGPASSWORD=password)
    query = subprocess.check_output([str(pg_bin / 'psql'), '-h', '127.0.0.1', '-p', str(args.postgres_port), '-U', 'openagent', '-d', 'postgres', '-tAc', "SELECT 1 FROM pg_database WHERE datname='openagent_local'"], env=pg_env, text=True).strip()
    if query != '1':
        subprocess.run([str(pg_bin / 'createdb'), '-h', '127.0.0.1', '-p', str(args.postgres_port), '-U', 'openagent', 'openagent_local'], env=pg_env, check=True)
    return f'Host=127.0.0.1;Port={args.postgres_port};Database=openagent_local;Username=openagent;Password={password}', data_root


def start(args):
    STATE.mkdir(parents=True, exist_ok=True)
    state_file = STATE / 'processes.json'
    if state_file.exists() and any(alive(item['pid']) for item in json.loads(state_file.read_text())):
        raise SystemExit('Preview is already running; run down before restarting.')
    executable = shutil.which(args.opencode)
    if not executable and args.opencode == 'opencode':
        executable = shutil.which(str(Path(args.data_root).expanduser() / 'tools/node_modules/.bin/opencode'))
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
    connection, data_root = database(args)
    backend_url = f'http://127.0.0.1:{args.engine_port}'
    web_url = f'http://127.0.0.1:{args.web_port}'
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=backend_url,
               OpenCode__Executable=executable, Cors__AllowedOrigins__0=web_url,
               ConnectionStrings__Redis='', ConnectionStrings__OpenAgentDatabase=connection,
               LocalDevelopment__FileStorageRoot=str(data_root / 'objects'),
               VITE_OPENAGENT_CONNECTION_MODE='engine',
               VITE_OPENAGENT_ROUTER_BASE_URL=backend_url, VITE_OPENAGENT_ENGINE_BASE_URL=backend_url)
    processes = []
    handles = []
    try:
        for name, command, cwd in (
            ('engine', ['dotnet', 'run', '--no-build', '--no-launch-profile', '--project', 'Backend/src/OpenAgent.Engine.Host'], ROOT),
            ('web', ['pnpm', 'dev', '--host', '127.0.0.1', '--port', str(args.web_port), '--strictPort'], frontend),
        ):
            with (STATE / f'{name}.log').open('w') as log:
                process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                           stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            handles.append(process)
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
                if time.monotonic() > deadline or not all(handle.poll() is None for handle in handles):
                    raise RuntimeError(f'Preview startup failed. Check {STATE} for logs.')
                time.sleep(0.5)
    except BaseException:
        stop()
        raise
    print(f'Preview: {web_url}\nEngine: {backend_url}\nLogs: {STATE}')
    print(f'PostgreSQL and local files persist across restarts. Data: {data_root}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['up', 'down', 'status'])
    parser.add_argument('--engine-port', type=int, default=5240)
    parser.add_argument('--web-port', type=int, default=5184)
    parser.add_argument('--opencode', default='opencode')
    parser.add_argument('--postgres-bin', default='')
    parser.add_argument('--postgres-port', type=int, default=55432)
    parser.add_argument('--database', default=os.environ.get('ConnectionStrings__OpenAgentDatabase', ''))
    parser.add_argument('--data-root', default=str(Path.home() / 'Library/Application Support/OpenAgent/local') if os.uname().sysname == 'Darwin' else str(Path.home() / '.local/share/openagent/local'))
    args = parser.parse_args()
    if args.action == 'up':
        start(args)
    elif args.action == 'down':
        stop()
    else:
        state_file = STATE / 'processes.json'
        for item in json.loads(state_file.read_text()) if state_file.exists() else []:
            print(item['name'], 'running' if alive(item['pid']) else 'stopped')
