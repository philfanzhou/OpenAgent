"""Verify the compact contract manifest against pinned upstream bytes (opt-in network)."""
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

root = Path(__file__).resolve().parent.parent
manifest = json.loads((root / 'Backend/tests/OpenAgent.Runtime.Acp.Tests/Fixtures/contracts.json').read_text())
base = f"https://raw.githubusercontent.com/agentclientprotocol/agent-client-protocol/{manifest['sourceCommit']}/schema/v1/"
inputs = {}
for name in ('schema', 'meta'):
    with urlopen(base + name + '.json', timeout=30) as response:
        data = response.read()
    assert hashlib.sha256(data).hexdigest() == manifest[name + 'Sha256'], name
    inputs[name] = json.loads(data)
meta, schema = inputs['meta'], inputs['schema']
assert manifest['methods'] == sorted(v for k in ('agentMethods', 'clientMethods', 'protocolMethods') for v in meta[k].values())
for key, definition, discriminator in [('updates', 'SessionUpdate', 'sessionUpdate'), ('content', 'ContentBlock', 'type')]:
    assert manifest[key] == sorted(x['properties'][discriminator]['const'] for x in schema['$defs'][definition]['oneOf'])
print('PASS: pinned schema/meta hashes and compact method/update/content contracts match upstream')
