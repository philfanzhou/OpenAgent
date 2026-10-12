"""Local deterministic OpenAI-compatible model; used only by the ACP preview."""
import json
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        data = json.dumps({'object': 'list', 'data': [{'id': 'preview', 'object': 'model', 'owned_by': 'local-preview'}]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0'))))
        messages = body.get('messages', [])
        last = messages[-1] if messages else {}
        content = last.get('content', '')
        if isinstance(content, list):
            content = '\n'.join(item.get('text', '') for item in content if isinstance(item, dict))
        tools = {item['function']['name'] for item in body.get('tools', []) if item.get('type') == 'function'}
        call = last.get('role') == 'user' and 'test tool' in content and 'bash' in tools
        text = 'OpenCode completed the preview tool call.' if last.get('role') == 'tool' else 'OpenCode ACP preview: ' + content[-200:]
        message = {'role': 'assistant', 'content': None if call else text}
        if call:
            message['tool_calls'] = [{'id': 'call_' + uuid.uuid4().hex, 'type': 'function', 'function': {'name': 'bash', 'arguments': json.dumps({'command': 'pwd', 'description': 'Inspect the ACP preview workspace'})}}]
        result = {'id': 'chatcmpl-' + uuid.uuid4().hex, 'object': 'chat.completion', 'created': int(time.time()), 'model': body.get('model', 'preview'),
                  'choices': [{'index': 0, 'message': message, 'finish_reason': 'tool_calls' if call else 'stop'}], 'usage': {'prompt_tokens': 8, 'completion_tokens': 8, 'total_tokens': 16}}
        self.send_response(200)
        if body.get('stream'):
            self.send_header('Content-Type', 'text/event-stream')
            self.end_headers()
            delta = message.copy()
            if call:
                delta['tool_calls'][0]['index'] = 0
            chunk = {**result, 'object': 'chat.completion.chunk', 'choices': [{'index': 0, 'delta': delta, 'finish_reason': None}]}
            self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode())
            chunk['choices'] = [{'index': 0, 'delta': {}, 'finish_reason': 'tool_calls' if call else 'stop'}]
            self.wfile.write(('data: ' + json.dumps(chunk) + '\n\ndata: [DONE]\n\n').encode())
        else:
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps(result).encode())

ThreadingHTTPServer(('0.0.0.0', 8099), Handler).serve_forever()
