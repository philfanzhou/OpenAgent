import io, re

def resolve(path, keeper):
    s = io.open(path, encoding='utf-8').read()
    pattern = re.compile(r'<<<<<<< HEAD\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n', re.S)
    def repl(m):
        head, incoming = m.group(1), m.group(2)
        return keeper(head, incoming)
    s2 = pattern.sub(repl, s)
    assert '<<<<<<<' not in s2 and '>>>>>>>' not in s2, path
    io.open(path, 'w', encoding='utf-8', newline='\n').write(s2)
    print('resolved', path)

# AGENTS: dev line + the runtime-assembly line (drop the console-era line)
resolve('AGENTS.md', lambda head, inc: head + ''.join(
    line + '\n' for line in inc.splitlines() if line.startswith('- Runtime 不发布 NuGet')))

# sln: dev side + incoming entries except the removed TestConsole GUID
resolve('Backend/OpenAgent.sln', lambda head, inc: head + ''.join(
    line + '\n' for line in inc.splitlines() if 'BA2588B0' not in line))

# sdk README: dev wording + the platform-consumption pointer line
resolve('docs/integrations/acp-dotnet-sdk/README.md', lambda head, inc: head + ''.join(
    line + '\n' for line in inc.splitlines() if line.startswith('平台如何消费本 SDK')))

# test reports index: keep the 2026-10-19 row, drop the removed 2026-10-16 row
resolve('docs/test-reports/README.md', lambda head, inc: head + ''.join(
    line + '\n' for line in inc.splitlines() if '2026-10-16' not in line))

# USAGE: dev closing line + adapted OpenCode section (console entry no longer exists)
def usage(head, inc):
    return head + '''## OpenCode

已安装并配置 OpenCode 的开发环境可用 SDK 客户端对接 `opencode acp` 子进程验证协议互通。平台采用独立环境、授权后的模型快照和每轮 MCP 桥，使用方式及无需付费模型的真实进程测试见 [OpenCode 运行时](../opencode-runtime.md)。
'''
resolve('docs/integrations/acp-dotnet-sdk/USAGE.md', usage)

# TODO: inspect separately
s = io.open('docs/planning/TODO.md', encoding='utf-8').read()
for m in re.finditer(r'<<<<<<< HEAD\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n', s, re.S):
    print('--- TODO HEAD ---'); print(m.group(1)[:400])
    print('--- TODO INC ---'); print(m.group(2)[:400])
