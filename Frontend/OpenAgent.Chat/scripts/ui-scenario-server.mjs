import { createServer } from 'node:http'

const port = 5208
const timestamp = '2026-08-12T14:00:00.000Z'

function message(messageId, sequence, role, content, extra = {}) {
  return { messageId, sequence, role, content, timestamp, ...extra }
}

function conversation(conversationId, title, messages, status = 'Completed') {
  return {
    conversationId,
    tenantId: 'development',
    userId: 'ui-tester',
    agentId: 'stepfun-assistant',
    status,
    createdAt: timestamp,
    updatedAt: timestamp,
    lastMessageAt: timestamp,
    messageCount: messages.length,
    title,
    messages,
  }
}

const conversations = [
  conversation('markdown-reasoning', 'Markdown 与思考过程', [
    message('m1', 1, 'user', '请用 Markdown 给出发布检查清单。'),
    message('m2', 2, 'assistant', '# 发布准备完成\n\n以下项目已经确认：\n\n- **构建**：生产包生成成功\n- **测试**：核心回归全部通过\n- **安全**：未在页面暴露凭据\n\n| 检查项 | 状态 |\n| --- | --- |\n| Frontend | ✅ Ready |\n| API | ✅ Ready |\n\n```bash\npnpm build\ndotnet build Backend/OpenAgent.sln\n```\n\n> 建议在发布前保留本次截图作为验收证据。', {
      metadata: { Reasoning: '先确认用户需要结构化的发布检查结果。\n整理构建、测试和安全三个维度，并用表格与代码块提高可读性。' },
    }),
  ]),
  conversation('tool-call', '工具调用状态', [
    message('t1', 1, 'user', '检查工作区并生成一份简短状态摘要。'),
    message('t2', 2, 'assistant', '', {
      toolName: 'shell_command',
      toolCallId: 'call-status',
      metadata: { ToolArguments: JSON.stringify({ command: 'git status --short', workdir: 'C:/MyData/Code/OpenAgent' }) },
    }),
    message('t3', 3, 'tool', ' M Frontend/OpenAgent.Chat/src/components/ChatMessages.vue\n M Frontend/OpenAgent.Chat/src/workspace.css', { toolName: 'shell_command', toolCallId: 'call-status' }),
    message('t4', 4, 'assistant', '检查完成。当前修改集中在聊天消息展示与样式文件中，未发现未解决的合并冲突。'),
  ]),
  conversation('file-flow', '文件上传与生成', [
    message('f1', 1, 'user', '请根据附件生成验收报告。', {
      metadata: { Files: JSON.stringify([{ fileId: 'requirements', fileName: 'requirements.md', mediaType: 'text/markdown', length: 1842 }]) },
    }),
    message('f2', 2, 'assistant', '已读取附件并生成报告。报告包含测试范围、结果摘要和遗留风险，可直接下载。', {
      metadata: { Files: JSON.stringify([{ fileId: 'acceptance-report', fileName: 'openagent-ui-acceptance.md', mediaType: 'text/markdown', length: 3268 }]) },
    }),
  ]),
  conversation('error-flow', '错误处理与 TraceId', [
    message('e1', 1, 'user', '调用一个当前不可用的外部工具。'),
    message('e2', 2, 'assistant', '', {
      error: {
        title: 'Agent 执行失败',
        detail: '工具服务暂时不可用，请检查 MCP 连接后重试。已保留本次请求上下文。',
        traceId: 'trace-ui-20260812-001',
      },
    }),
  ], 'Failed'),
]

const fileBodies = {
  requirements: '# Requirements\n\n- Markdown rendering\n- File upload feedback\n- Error trace visibility\n',
  'acceptance-report': '# OpenAgent UI Acceptance\n\nAll visual scenarios were rendered successfully.\n',
}

function headers(contentType = 'application/json; charset=utf-8') {
  return {
    'Access-Control-Allow-Headers': 'Authorization, Content-Type, X-Tenant-Id, X-Trace-Id, X-Conversation-Id',
    'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
    'Access-Control-Allow-Origin': 'http://localhost:5173',
    'Content-Type': contentType,
  }
}

function sendJson(response, body, status = 200) {
  response.writeHead(status, headers())
  response.end(JSON.stringify(body))
}

const server = createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://localhost:${port}`)
  if (request.method === 'OPTIONS') {
    response.writeHead(204, headers())
    return response.end()
  }
  if (url.pathname === '/health' || url.pathname === '/ready') return sendJson(response, { status: 'Healthy', entries: {} })
  if (url.pathname === '/api/v1/auth/config') return sendJson(response, { mode: 'Basic', password: { enabled: true } })
  if (url.pathname === '/api/v1/agent/me') return sendJson(response, { userId: 'ui-tester', tenantId: 'development', isAuthenticated: true })
  if (url.pathname === '/api/v1/agent/agents') return sendJson(response, [{ agentId: 'stepfun-assistant', name: 'StepFun Assistant', description: 'step-3.7-flash visual test agent', apiFormat: 'OpenAIChatCompletions', currentVersion: 'ui-test' }])
  if (url.pathname === '/api/v1/agent/conversations') {
    return sendJson(response, conversations.map(({ messages, ...summary }) => summary))
  }
  if (url.pathname.startsWith('/api/v1/agent/conversations/')) {
    const id = decodeURIComponent(url.pathname.split('/').pop() ?? '')
    const item = conversations.find(value => value.conversationId === id)
    return item ? sendJson(response, item) : sendJson(response, { detail: 'Conversation not found' }, 404)
  }
  if (request.method === 'POST' && url.pathname === '/api/v1/agent/files') {
    return setTimeout(() => sendJson(response, {
      fileId: 'uploaded-ui-file',
      tenantId: 'development',
      ownerUserId: 'ui-tester',
      fileName: 'ui-upload.md',
      mediaType: 'text/markdown',
      length: 96,
      sha256: 'visual-test',
      source: 'UserUpload',
      state: 'Ready',
      createdAt: timestamp,
    }), 900)
  }
  const fileMatch = url.pathname.match(/^\/api\/v1\/agent\/files\/([^/]+)\/(content|download)$/)
  if (fileMatch) {
    const body = fileBodies[decodeURIComponent(fileMatch[1])] ?? '# UI upload\n\nThe uploaded test file is ready.\n'
    response.writeHead(200, headers('text/markdown; charset=utf-8'))
    return response.end(body)
  }
  if (request.method === 'POST' && url.pathname === '/api/v1/agent/chat/stream') {
    response.writeHead(200, headers('text/event-stream; charset=utf-8'))
    response.write(`event: reasoning\ndata: ${JSON.stringify({ content: '正在整理附件与请求内容。' })}\n\n`)
    response.write(`event: content\ndata: ${JSON.stringify({ content: '## 已收到\n\n文件上传成功，视觉回归场景运行正常。' })}\n\n`)
    response.write(`event: done\ndata: ${JSON.stringify({ done: true, status: 'Completed', conversationId: 'markdown-reasoning' })}\n\n`)
    return response.end()
  }
  return sendJson(response, { detail: `No fixture for ${request.method} ${url.pathname}`, traceId: 'trace-ui-fixture' }, 404)
})

server.listen(port, '127.0.0.1', () => {
  process.stdout.write(`OpenAgent UI scenario server listening on http://localhost:${port}\n`)
})
