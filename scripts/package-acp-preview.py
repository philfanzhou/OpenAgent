#!/usr/bin/env python3
"""Export only preview images and launch assets; never export containers or data volumes."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile

args = argparse.ArgumentParser()
args.add_argument('output', type=pathlib.Path)
args.add_argument('--local', action='store_true', help='Also package the published host Engine and local preview images')
options = args.parse_args()
out = options.output.resolve()
out.mkdir(parents=True, exist_ok=True)
if any(out.iterdir()):
    args.error('The output directory must be empty so existing files cannot enter the preview package.')
compose = json.loads(subprocess.check_output(['docker', 'compose', '-p', 'openagent-acp-preview', '-f', 'deploy/acp-preview/compose.yml', 'config', '--format', 'json']))
compose.pop('name', None)
for volume in compose.get('volumes', {}).values():
    volume.pop('name', None)
for network in compose.get('networks', {}).values():
    network.pop('name', None)
images = []
for name, service in compose['services'].items():
    service.pop('build', None)
    if name in ['engine', 'router', 'chat']:
        service['image'] = 'openagent-acp-preview-' + name
    images.append(service['image'])
    port_variables = {'engine': ('ACP_ENGINE_PORT', '5276'), 'router': ('ACP_ROUTER_PORT', '5076'), 'chat': ('ACP_CHAT_PORT', '8176')}
    if name in port_variables:
        variable, default = port_variables[name]
        service['ports'][0]['published'] = '${' + variable + ':-' + default + '}'
    if name in ['engine', 'router']:
        service['environment']['Cors__AllowedOrigins__0'] = 'http://localhost:${ACP_CHAT_PORT:-8176}'
    if name == 'mock-model':
        service['volumes'][0]['source'] = './mock-model.py'
for rel in ['scripts/acp-preview.sh', 'scripts/test-acp-preview.py', 'deploy/acp-preview/mock-model.py', 'docs/integrations/runtime-adapters.md']:
    destination = out / rel
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(rel, destination)
(out / 'deploy/acp-preview/offline.compose.json').write_text(json.dumps(compose, indent=2))
if options.local:
    local = json.loads(subprocess.check_output(['docker', 'compose', '-p', 'openagent-acp-local', '-f', 'deploy/acp-preview/compose.local.yml', 'config', '--format', 'json']))
    local.pop('name', None)
    for item in [*local.get('volumes', {}).values(), *local.get('networks', {}).values()]:
        item.pop('name', None)
    defaults = {'postgres': ('DB', '5477'), 'redis': ('REDIS', '6387'), 'router': ('ROUTER', '5077'), 'chat': ('CHAT', '8177')}
    for name, service in local['services'].items():
        service.pop('build', None)
        if name in ['router', 'chat']:
            service['image'] = 'openagent-acp-local-' + name
        images.append(service['image'])
        variable, default = defaults[name]
        service['ports'][0]['published'] = '${ACP_LOCAL_' + variable + '_PORT:-' + default + '}'
    local['services']['router']['environment']['RouterSettings__Routing__EngineEndpoint'] = 'http://host.docker.internal:${ACP_LOCAL_ENGINE_PORT:-5277}'
    local['services']['router']['environment']['Cors__AllowedOrigins__0'] = 'http://localhost:${ACP_LOCAL_CHAT_PORT:-8177}'
    (out / 'deploy/acp-preview/compose.local.yml').write_text(json.dumps(local, indent=2))
    for rel in ['scripts/acp-local-preview.py', 'scripts/acp-demo-harness.py']:
        shutil.copy2(rel, out / rel)
    # Framework-dependent build output only; never copy workspaces, login homes, logs or databases.
    with tempfile.TemporaryDirectory(prefix='acp-preview-publish-') as staging:
        subprocess.run(['dotnet', 'publish', 'Backend/src/OpenAgent.Engine.Host', '-c', 'Release',
            '--no-build', '--no-restore', '-o', staging], check=True)
        shutil.copytree(staging, out / 'prebuilt-engine')
    (out / 'start-local.sh').write_text('''#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
shasum -a 256 -c images.tar.sha256
docker image load -i images.tar
python3 scripts/acp-local-preview.py up
''')
    (out / 'start-local.sh').chmod(0o755)
subprocess.run(['docker', 'image', 'save', '-o', str(out / 'images.tar'), *dict.fromkeys(images)], check=True)
(out / 'manifest.json').write_text(json.dumps({'revision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(), 'harness': 'Generic ACP; OpenCode v1.19.0; local Claude bridge 0.89.1' if options.local else 'OpenCode v1.19.0', 'localPreviewIncluded': options.local, 'architecture': json.loads(subprocess.check_output(['docker', 'image', 'inspect', 'openagent-acp-preview-engine']))[0]['Architecture'], 'images': images, 'containsUserData': False}, indent=2))
archive = out / 'images.tar'
with archive.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
(out / 'images.tar.sha256').write_text(digest + '  images.tar\n')
(out / 'start.sh').write_text('''#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
shasum -a 256 -c images.tar.sha256
docker image load -i images.tar
export ACP_PREVIEW_COMPOSE_FILE=deploy/acp-preview/offline.compose.json
bash scripts/acp-preview.sh up
''')
(out / 'start.sh').chmod(0o755)
print('Preview package:', out)
