#!/usr/bin/env python3
"""Seed and exercise the isolated ACP preview, including real OpenCode with a local mock model."""
import argparse
import json
import os
import time
import urllib.request
import urllib.error
import uuid

base = 'http://localhost:' + os.getenv('ACP_ENGINE_PORT', '5276')

def request(path, data=None, method=None):
    payload = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(base + path, payload, {'Content-Type': 'application/json'}, method=method)
    with urllib.request.urlopen(req, timeout=180) as response:
        return json.load(response)

def ready():
    for _ in range(90):
        try:
            request('/api/v1/admin/agents')
            return
        except (OSError, ValueError):
            time.sleep(2)
    raise RuntimeError('ACP preview engine did not become ready')

def seed():
    request('/api/v1/admin/llm/acp-preview', {
        'id': 'acp-preview', 'name': 'ACP preview local model (no AI)', 'format': 'OpenAIChatCompletions',
        'modelId': 'preview', 'endpoint': 'http://mock-model:8099/v1', 'apiKey': 'preview', 'contextTokens': 32768, 'temperature': 0
    }, 'PUT')
    for harness in ['demo', 'opencode']:
        agent = 'acp-' + harness
        try:
            existing = request('/api/v1/admin/agents/' + agent)
            version = existing.get('currentVersion', '')
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            version = ''
        request('/api/v1/admin/agents/' + agent + '/config', {
            'agentId': agent, 'name': 'OpenCode ACP preview' if harness == 'opencode' else 'ACP protocol demo (no AI)',
            'description': 'Local ACP preview', 'status': 2, 'currentVersion': version,
            'config': {'instructions': 'Answer concisely. Use bash when asked to inspect the working directory.',
                'runtime': {'adapter': 'acp', 'harness': harness}, 'maxTurns': 5,
                'mcp': {'servers': []}, 'skills': {'enabledSkills': [], 'instances': []},
                'rag': {'enabled': False}, 'codeExecution': {'enabled': harness == 'opencode'}}
        }, 'PUT')

def stream(agent, conversation, message):
    data = {'message': message, 'context': {'agentId': agent, 'llmProfileId': 'acp-preview', 'conversationId': conversation}}
    req = urllib.request.Request(base + '/api/v1/agent/chat/stream', json.dumps(data).encode(), {'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=180) as response:
        text = response.read().decode()
    events = []
    for block in text.replace('\r\n', '\n').split('\n\n'):
        lines = block.splitlines()
        name = next((line[6:].strip() for line in lines if line.startswith('event:')), '')
        payload = '\n'.join(line[5:].strip() for line in lines if line.startswith('data:'))
        if payload:
            events.append({'type': name, **json.loads(payload)})
    errors = [event for event in events if event.get('type') == 'error']
    if errors:
        raise RuntimeError('ACP preview stream failed: ' + json.dumps(errors))
    assert any(event.get('type') == 'done' for event in events), 'Missing completion event'
    return events

def main():
    args = argparse.ArgumentParser()
    args.add_argument('--seed', action='store_true')
    options = args.parse_args()
    ready()
    if options.seed:
        seed()
    runtimes = request('/api/v1/agent/runtimes')
    assert {'demo', 'opencode'} <= {item.get('harness') for item in runtimes}
    demo = str(uuid.uuid4())
    first = stream('acp-demo', demo, 'hello')
    second = stream('acp-demo', demo, 'again')
    assert any('turn 1' in event.get('content', '') for event in first)
    assert any('turn 2' in event.get('content', '') for event in second)
    permission = stream('acp-demo', str(uuid.uuid4()), '/permission')
    assert any(event.get('type') == 'tool_result' for event in permission)
    native = str(uuid.uuid4())
    stream('acp-opencode', native, 'hello from the ACP preview')
    stream('acp-opencode', native, 'continue the same session')
    tool = stream('acp-opencode', str(uuid.uuid4()), 'test tool')
    assert any(event.get('type') == 'tool_call' for event in tool), 'OpenCode tool call was not streamed'
    assert any(event.get('type') == 'tool_result' for event in tool), 'OpenCode tool result was not streamed'
    print('PASS: runtime catalog, ACP demo, permission round-trip, session reload, real OpenCode prompt and tool loop with local mock model')
    print('Preview: http://localhost:' + os.getenv('ACP_CHAT_PORT', '8176'))

if __name__ == '__main__':
    main()
