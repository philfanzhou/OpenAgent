import asyncio
import os
import sys
from acp import Agent, Client, run_agent, spawn_agent_process
from acp.schema import (AgentMessageChunk, InitializeResponse, AgentCapabilities,
                        NewSessionResponse, PromptResponse, TextContentBlock,
                        RequestPermissionResponse, ClientCapabilities)

class Echo(Agent):
    def __init__(self):
        self.waiting = {}
    def on_connect(self, conn):
        self.conn = conn
    async def initialize(self, protocol_version, **kwargs):
        return InitializeResponse(protocol_version=1, agent_capabilities=AgentCapabilities())
    async def new_session(self, cwd, **kwargs):
        return NewSessionResponse(session_id='python-session')
    async def prompt(self, session_id, prompt, **kwargs):
        slow = prompt[0].text == 'slow'
        event = self.waiting[session_id] = asyncio.Event()
        await self.conn.session_update(session_id, AgentMessageChunk(content=TextContentBlock(type='text', text='started' if slow else '你好🥐')))
        if slow:
            await event.wait()
        return PromptResponse(stop_reason='cancelled' if slow else 'end_turn')
    async def cancel(self, session_id, **kwargs):
        self.waiting[session_id].set()

class Probe(Client):
    def __init__(self):
        self.text = ''
        self.started = asyncio.Event()
    async def session_update(self, session_id, update, **kwargs):
        if isinstance(update, AgentMessageChunk):
            self.text += update.content.text
            if update.content.text == 'started':
                self.started.set()
    async def request_permission(self, **kwargs):
        return RequestPermissionResponse(outcome={'outcome': 'cancelled'})

async def main():
    if sys.argv[1] == 'agent':
        await run_agent(Echo())
        return
    probe = Probe()
    async with spawn_agent_process(probe, sys.argv[2], *sys.argv[3:]) as (conn, proc):
        assert (await conn.initialize(protocol_version=1, client_capabilities=ClientCapabilities())).protocol_version == 1
        session = await conn.new_session(cwd=os.getcwd(), mcp_servers=[])
        async def prompt(text):
            return await conn.prompt(session_id=session.session_id, prompt=[TextContentBlock(type='text', text=text)])
        assert (await prompt('你好🥐')).stop_reason == 'end_turn'
        assert probe.text == 'Echo: 你好🥐'
        probe.text = ''
        assert (await prompt('permission')).stop_reason == 'end_turn'
        assert probe.text == 'cancelled'
        task = asyncio.create_task(prompt('slow'))
        await asyncio.wait_for(probe.started.wait(), 5)
        await conn.cancel(session_id=session.session_id)
        assert (await asyncio.wait_for(task, 5)).stop_reason == 'cancelled'
        print('PASS Python client -> C# agent: unicode, reverse permission, confirmed cancellation')
asyncio.run(main())
