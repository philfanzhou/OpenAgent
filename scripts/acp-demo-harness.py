#!/usr/bin/env python3
"""Deterministic ACP v1 harness for adapter testing; no model calls or file tools."""
import json
import os
import pathlib
import sys
import uuid

session = None
pending = None
turns = 0
state = pathlib.Path.cwd() / '.acp-demo-session.json'

def send(value):
    print(json.dumps({'jsonrpc': '2.0', **value}), flush=True)

def reply(request, result):
    send({'id': request['id'], 'result': result})

def update(value):
    send({'method': 'session/update', 'params': {'sessionId': session, 'update': value}})

def finish(request, text):
    global turns
    turns += 1
    state.write_text(json.dumps({'id': session, 'turns': turns}))
    update({'sessionUpdate': 'agent_message_chunk', 'content': {'type': 'text', 'text': text}})
    reply(request, {'stopReason': 'end_turn', 'usage': {'inputTokens': 3, 'outputTokens': 5, 'totalTokens': 8}})

def options():
    if os.getenv('ACP_DEMO_NO_MODELS') == '1':
        return {}
    return {'configOptions': [{'id': 'model', 'name': 'Model', 'type': 'select', 'category': 'model', 'currentValue': 'preview/preview', 'options': []}]}

for line in sys.stdin:
    request = json.loads(line)
    method = request.get('method')
    params = request.get('params', {})
    if method == 'initialize':
        reply(request, {'protocolVersion': 1, 'agentCapabilities': {'loadSession': True}, 'agentInfo': {'name': 'ACP protocol demo', 'version': '1'}})
    elif method == 'session/new':
        session = str(uuid.uuid4())
        reply(request, {'sessionId': session, **options()})
    elif method == 'session/load':
        saved = json.loads(state.read_text())
        assert saved['id'] == params['sessionId']
        session, turns = saved['id'], saved['turns']
        update({'sessionUpdate': 'agent_message_chunk', 'content': {'type': 'text', 'text': 'HISTORY REPLAY MUST NOT BE DUPLICATED'}})
        reply(request, options())
    elif method in ('session/set_config_option', 'session/set_model'):
        reply(request, options())
    elif method == 'session/prompt':
        text = ''.join(item.get('text', '') for item in params['prompt'])
        text = text.split('User request:\n')[-1]
        if text == '/crash':
            print('invalid protocol output', flush=True)
            sys.exit(9)
        elif text == '/oversized':
            print('x' * (1024 * 1024 + 1), flush=True)
        elif text == '/slow':
            pending = request
            update({'sessionUpdate': 'agent_message_chunk', 'content': {'type': 'text', 'text': 'partial before cancellation'}})
        elif text == '/permission':
            pending = request
            update({'sessionUpdate': 'tool_call', 'toolCallId': 'demo-tool', 'name': 'demo', 'title': 'Protocol demo tool', 'status': 'pending', 'rawInput': {'demo': True}})
            send({'id': 'permission-1', 'method': 'session/request_permission', 'params': {'sessionId': session, 'toolCall': {'toolCallId': 'demo-tool'}, 'options': [{'optionId': 'once', 'kind': 'allow_once', 'name': 'Allow once'}, {'optionId': 'reject', 'kind': 'reject_once', 'name': 'Reject'}]}})
        elif text == '/plan':
            update({'sessionUpdate': 'plan', 'entries': [{'content': 'Test ACP', 'priority': 'medium', 'status': 'completed'}]})
            finish(request, 'Plan updated.')
        elif text == '/environment':
            finish(request, 'isolated' if 'ACP_TEST_FOREIGN_SECRET' not in os.environ else 'LEAKED')
        else:
            finish(request, f'ACP demo turn {turns + 1}: {text}')
    elif method == 'session/cancel':
        if pending:
            reply(pending, {'stopReason': 'cancelled'})
            pending = None
    elif request.get('id') == 'permission-1':
        outcome = request['result']['outcome']
        decision = outcome.get('optionId', 'cancelled')
        update({'sessionUpdate': 'tool_call_update', 'toolCallId': 'demo-tool', 'status': 'completed', 'rawOutput': decision})
        finish(pending, f'Permission decision: {decision}')
        pending = None
    elif 'id' in request:
        send({'id': request['id'], 'error': {'code': -32601, 'message': 'Method not found'}})
