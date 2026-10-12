#!/usr/bin/env python3
"""Host Engine preview for locally authenticated ACP tools; no host credentials in Docker."""
import argparse
import json
import os
import pathlib
import shutil
import signal
import subprocess
import time
import urllib.request
import urllib.error
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
STATE = ROOT / '.preview' / 'acp-local'
PROJECT = os.getenv('ACP_LOCAL_PROJECT', 'openagent-acp-local')
ENGINE_PORT = os.getenv('ACP_LOCAL_ENGINE_PORT', '5277')
BASE = 'http://localhost:' + ENGINE_PORT
COMPOSE = ['docker', 'compose', '-p', PROJECT, '-f', str(ROOT / 'deploy/acp-preview/compose.local.yml')]


def run(command):
    subprocess.run(command, cwd=ROOT, check=True)


def request(path, data=None, method=None):
    payload = json.dumps(data).encode() if data is not None else None
    with urllib.request.urlopen(urllib.request.Request(BASE + path, payload,
            {'Content-Type': 'application/json'}, method=method), timeout=90) as response:
        return json.load(response)


def running():
    pidfile = STATE / 'engine.pid'
    if not pidfile.exists():
        return None
    pid = int(pidfile.read_text())
    result = subprocess.run(['ps', '-p', str(pid), '-o', 'args='], capture_output=True, text=True)
    return pid if str(STATE / 'engine/OpenAgent.Engine.Host.dll') in result.stdout else None


def harnesses():
    bridge = ROOT / '.preview/claude-acp/node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js'
    found = [{'Id': 'demo', 'Name': 'ACP protocol demo (no AI)', 'Command': shutil.which('python3'),
        'Arguments': [str(ROOT / 'scripts/acp-demo-harness.py')],
        'Environment': {'ACP_DEMO_NO_MODELS': '1'}, 'PermissionPolicy': 'deny'}]
    claude = shutil.which('claude')
    if claude:
        if not bridge.exists():
            run(['npm', 'install', '--prefix', str(ROOT / '.preview/claude-acp'), '--no-audit', '--no-fund',
                '@agentclientprotocol/claude-agent-acp@0.89.1'])
        found.append({'Id': 'claude-local', 'Name': 'Claude Code (local ACP)', 'Command': shutil.which('node'),
            'Arguments': [str(bridge)], 'EnvironmentMode': 'local',
            'Environment': {'CLAUDE_CODE_EXECUTABLE': claude},
            'InheritedEnvironmentVariables': ['ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL',
                'ANTHROPIC_MODEL', 'CLAUDE_CODE_OAUTH_TOKEN'],
            'PermissionPolicy': 'allow_once'})
    gemini = shutil.which('gemini')
    if gemini:
        found.append({'Id': 'gemini-local', 'Name': 'Gemini CLI (local ACP)', 'Command': gemini,
            'Arguments': ['--experimental-acp'], 'EnvironmentMode': 'local',
            'InheritedEnvironmentVariables': ['GEMINI_API_KEY', 'GOOGLE_API_KEY', 'GOOGLE_CLOUD_PROJECT'],
            'PermissionPolicy': 'allow_once'})
    opencode = shutil.which('opencode')
    if opencode:
        found.append({'Id': 'opencode-local', 'Name': 'OpenCode (local ACP)', 'Command': opencode,
            'Arguments': ['acp'], 'EnvironmentMode': 'local', 'PermissionPolicy': 'allow_once'})
    if os.getenv('ACP_LOCAL_HARNESSES_FILE'):
        found.extend(json.loads(pathlib.Path(os.environ['ACP_LOCAL_HARNESSES_FILE']).read_text()))
    return found


def seed(choices):
    for harness in choices:
        agent = 'acp-' + harness['Id']
        try:
            version = request('/api/v1/admin/agents/' + agent).get('currentVersion', '')
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            version = ''
        request('/api/v1/admin/agents/' + agent + '/config', {
            'agentId': agent, 'name': harness['Name'], 'description': 'Local ACP runtime preview',
            'status': 2, 'currentVersion': version, 'config': {
                'runtime': {'adapter': 'acp', 'harness': harness['Id']}, 'instructions': 'Answer concisely.',
                'maxTurns': 10, 'mcp': {'servers': []}, 'skills': {'enabledSkills': [], 'instances': []},
                'rag': {'enabled': False}, 'codeExecution': {'enabled': False}}}, 'PUT')


def stream(agent, conversation, message, endpoint=BASE):
    # No platform model id or API key: model and login belong to the local harness.
    data = {'message': message, 'context': {'agentId': agent, 'conversationId': conversation}}
    req = urllib.request.Request(endpoint + '/api/v1/agent/chat/stream', json.dumps(data).encode(),
        {'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=180) as response:
        raw = response.read().decode()
    events = []
    for block in raw.replace('\r\n', '\n').split('\n\n'):
        lines = block.splitlines()
        name = next((line[6:].strip() for line in lines if line.startswith('event:')), '')
        payload = '\n'.join(line[5:].strip() for line in lines if line.startswith('data:'))
        if payload:
            events.append({'type': name, **json.loads(payload)})
    errors = [event for event in events if event.get('type') == 'error']
    if errors:
        raise RuntimeError(json.dumps(errors))
    if not any(event.get('type') == 'done' for event in events):
        raise RuntimeError('Missing completion event')
    return events


def test(real=False):
    catalog = request('/api/v1/agent/runtimes')
    native = [item for item in catalog if item['adapter'] == 'acp' and not item['requiresLlmProfile']]
    assert native, 'No native harness registered'
    for item in native:
        probe = request('/api/v1/agent/runtimes/' + item['harness'] + '/test', method='POST')
        print(item['harness'], probe['stage'], 'PASS' if probe['success'] else 'FAIL')
        if not probe['success']:
            raise RuntimeError(probe['message'])
    conversation = str(uuid.uuid4())
    stream('acp-demo', conversation, 'hello')
    second = stream('acp-demo', conversation, 'again')
    assert any('turn 2' in item.get('content', '') for item in second)
    print('PASS: native model ownership, minimal ACP agent without model configuration, session reload')
    if real:
        assert any(item['harness'] == 'claude-local' for item in native), 'Claude CLI is not installed'
        conversation = str(uuid.uuid4())
        first = stream('acp-claude-local', conversation, 'Remember the marker ACP-LOCAL-42. Reply exactly ACP-LOCAL-42.')
        second = stream('acp-claude-local', conversation, 'What marker did I ask you to remember? Reply only the marker.')
        assert all('ACP-LOCAL-42' in ''.join(item.get('content', '') for item in events if item['type'] == 'content') for events in [first, second])
        tools = stream('acp-claude-local', str(uuid.uuid4()), 'Use your Bash tool to run pwd. Then reply briefly with the directory. Do not use other tools.')
        assert any(item.get('type') == 'tool_call' for item in tools)
        assert any(item.get('type') == 'tool_result' for item in tools)
        router = 'http://localhost:' + os.getenv('ACP_LOCAL_ROUTER_PORT', '5077')
        stream('acp-claude-local', str(uuid.uuid4()), 'Reply exactly ACP-ROUTER-OK.', router)
        print('PASS: real local Claude login/model, streaming, persisted continuation, native Bash permission/tool loop, Router forwarding')


def up():
    if running():
        raise RuntimeError('This local preview Engine is already running; use down before rebuilding')
    STATE.mkdir(parents=True, exist_ok=True)
    choices = harnesses()
    run(COMPOSE + ['up', '-d', '--build', '--wait'])
    run(['dotnet', 'publish', 'Backend/src/OpenAgent.Engine.Host', '-c', 'Release', '-o', str(STATE / 'engine')])
    env = os.environ.copy()
    # Clear previously configured harness arrays so this preview owns its registration.
    for key in list(env):
        if key.startswith('AgentRuntimes__'):
            del env[key]
    env.update({
        'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': 'http://127.0.0.1:' + ENGINE_PORT,
        'ConnectionStrings__OpenAgentDatabase': 'Host=localhost;Port=' + os.getenv('ACP_LOCAL_DB_PORT', '5477') + ';Database=openagent;Username=openagent;Password=preview-local-only',
        'ConnectionStrings__Redis': 'localhost:' + os.getenv('ACP_LOCAL_REDIS_PORT', '6387'),
        'Database__ApplyMigrationsOnStartup': 'true', 'Authentication__Mode': 'Basic',
        'Authentication__EnableKeycloak': 'false', 'Authentication__AllowDevelopmentAnonymous': 'true',
        'Authorization__Mode': 'AllowAll', 'FileAssets__Enabled': 'false', 'CodeExecution__Enabled': 'false',
        'Heartbeat__AdvertisedHost': 'host.docker.internal', 'Heartbeat__AdvertisedPort': ENGINE_PORT,
        'AgentRuntimes__Acp__Enabled': 'true', 'AgentRuntimes__Acp__LocalTrusted': 'true',
        'AgentRuntimes__Acp__WorkspaceRoot': str(STATE / 'workspaces'),
        'Cors__AllowedOrigins__0': 'http://localhost:' + os.getenv('ACP_LOCAL_CHAT_PORT', '8177')})
    def flatten(prefix, value):
        if isinstance(value, dict):
            for key, item in value.items():
                flatten(prefix + '__' + key, item)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                flatten(prefix + '__' + str(index), item)
        else:
            env[prefix] = str(value).lower() if isinstance(value, bool) else str(value)
    flatten('AgentRuntimes__Acp__Harnesses', choices)
    with (STATE / 'engine.log').open('a') as log:
        process = subprocess.Popen(['dotnet', str(STATE / 'engine/OpenAgent.Engine.Host.dll')], cwd=STATE / 'engine',
            env=env, stdout=log, stderr=log, start_new_session=True)
    (STATE / 'engine.pid').write_text(str(process.pid))
    for _ in range(90):
        if process.poll() is not None:
            raise RuntimeError('Engine exited. See .preview/acp-local/engine.log')
        try:
            request('/api/v1/admin/agents')
            break
        except (OSError, ValueError):
            time.sleep(1)
    else:
        raise RuntimeError('Engine did not become ready. See .preview/acp-local/engine.log')
    seed(choices)
    test()
    print('Preview: http://localhost:' + os.getenv('ACP_LOCAL_CHAT_PORT', '8177'))


def down():
    pid = running()
    if pid:
        os.kill(pid, signal.SIGTERM)
        for _ in range(20):
            if not running():
                break
            time.sleep(0.25)
    run(COMPOSE + ['down'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['up', 'test', 'status', 'down'], nargs='?', default='up')
    parser.add_argument('--real', action='store_true', help='Call the real locally authenticated Claude model')
    args = parser.parse_args()
    if args.action == 'up':
        up()
    elif args.action == 'down':
        down()
    elif args.action == 'test':
        test(args.real)
    else:
        print('Host Engine:', running() or 'stopped')
        run(COMPOSE + ['ps'])
