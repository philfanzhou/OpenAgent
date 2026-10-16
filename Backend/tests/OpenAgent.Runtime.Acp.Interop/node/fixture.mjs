import { spawn } from 'node:child_process';
import { Readable, Writable } from 'node:stream';
import assert from 'node:assert/strict';
import * as acp from '@agentclientprotocol/sdk';

const stream = (input, output) => acp.ndJsonStream(Writable.toWeb(input), Readable.toWeb(output));
if (process.argv[2] === 'agent') {
  const waiting = new Map();
  await acp.agent()
    .onRequest(acp.methods.agent.initialize, () => ({ protocolVersion: 1, agentCapabilities: {} }))
    .onRequest(acp.methods.agent.session.new, () => ({ sessionId: 'ts-session' }))
    .onRequest(acp.methods.agent.session.prompt, async ctx => {
      if (ctx.params.prompt[0].text === 'slow') {
        const cancelled = new Promise(resolve => waiting.set(ctx.params.sessionId, resolve));
        await ctx.client.notify(acp.methods.client.session.update, { sessionId: ctx.params.sessionId, update: { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'started' } } });
        await cancelled;
        return { stopReason: 'cancelled' };
      }
      await ctx.client.notify(acp.methods.client.session.update, { sessionId: ctx.params.sessionId, update: { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: '你好🥐' } } });
      return { stopReason: 'end_turn' };
    })
    .onNotification(acp.methods.agent.session.cancel, ctx => waiting.get(ctx.params.sessionId)?.())
    .connect(stream(process.stdout, process.stdin));
} else {
  const child = spawn(process.argv[3], process.argv.slice(4), { stdio: ['pipe', 'pipe', 'inherit'] });
  let text = '', cancel;
  try {
    await acp.client()
      .onNotification(acp.methods.client.session.update, ctx => {
        if (ctx.params.update.sessionUpdate === 'agent_message_chunk') {
          text += ctx.params.update.content.text;
          if (ctx.params.update.content.text === 'started') cancel?.();
        }
      })
      .onRequest(acp.methods.client.session.requestPermission, () => ({ outcome: { outcome: 'cancelled' } }))
      .connectWith(stream(child.stdin, child.stdout), async ctx => {
        assert.equal((await ctx.request(acp.methods.agent.initialize, { protocolVersion: 1, clientCapabilities: {} })).protocolVersion, 1);
        const s = await ctx.request(acp.methods.agent.session.new, { cwd: process.cwd(), mcpServers: [] });
        const prompt = value => ctx.request(acp.methods.agent.session.prompt, { sessionId: s.sessionId, prompt: [{ type: 'text', text: value }] });
        assert.equal((await prompt('你好🥐')).stopReason, 'end_turn'); assert.equal(text, 'Echo: 你好🥐'); text = '';
        assert.equal((await prompt('permission')).stopReason, 'end_turn'); assert.equal(text, 'cancelled');
        cancel = () => { void ctx.notify(acp.methods.agent.session.cancel, { sessionId: s.sessionId }); };
        assert.equal((await prompt('slow')).stopReason, 'cancelled');
        console.log('PASS TypeScript client -> C# agent: unicode, reverse permission, confirmed cancellation');
      });
  } finally { child.kill(); }
}
