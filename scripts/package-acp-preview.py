#!/usr/bin/env python3
"""Export only preview images and launch assets; never export containers or data volumes."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

args = argparse.ArgumentParser()
args.add_argument('output', type=pathlib.Path)
options = args.parse_args()
out = options.output.resolve()
out.mkdir(parents=True, exist_ok=True)
compose = json.loads(subprocess.check_output(['docker', 'compose', '-p', 'openagent-acp-preview', '-f', 'deploy/acp-preview/compose.yml', 'config', '--format', 'json']))
images = []
for name, service in compose['services'].items():
    service.pop('build', None)
    if name in ['engine', 'router', 'chat']:
        service['image'] = 'openagent-acp-preview-' + name
    images.append(service['image'])
    if name == 'mock-model':
        service['volumes'][0]['source'] = './mock-model.py'
for rel in ['scripts/acp-preview.sh', 'scripts/test-acp-preview.py', 'deploy/acp-preview/mock-model.py', 'docs/integrations/runtime-adapters.md']:
    destination = out / rel
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(rel, destination)
(out / 'deploy/acp-preview/offline.compose.json').write_text(json.dumps(compose, indent=2))
subprocess.run(['docker', 'image', 'save', '-o', str(out / 'images.tar'), *dict.fromkeys(images)], check=True)
(out / 'manifest.json').write_text(json.dumps({'revision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(), 'harness': 'OpenCode v1.19.0', 'images': images, 'containsUserData': False}, indent=2))
archive = out / 'images.tar'
digest = hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest()
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
