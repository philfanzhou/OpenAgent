#!/usr/bin/env python3
"""Offline byte comparison and fail-closed lock probes. Build CodeGen before running."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

root = Path(sys.argv[1] if len(sys.argv) > 1 else '.').resolve()
report_path = Path(sys.argv[2]) if len(sys.argv) > 2 else root / 'codegen-verification.json'
project = root / 'Backend/tools/OpenAgent.Runtime.Acp.CodeGen'
configuration = sys.argv[3] if len(sys.argv) > 3 else 'Debug'
output = root / 'Backend/src/OpenAgent.Runtime.Acp/Protocol/Generated'
def snapshot():
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.iterdir()) if p.is_file()}
def run(target):
    result = subprocess.run(['dotnet', 'run', '--configuration', configuration, '--no-build', '--project', str(project), '--', str(target)], cwd=root, capture_output=True, text=True, timeout=60)
    return {'exitCode': result.returncode, 'stdout': result.stdout.strip(), 'stderr': result.stderr.strip()}

before = snapshot()
first = run(root)
after_first = snapshot()
second = run(root)
after_second = snapshot()
probes = []
for relative in ['schema/v1/schema.json', 'schema/v1/meta.json', 'schema/v1/LICENSE', 'schema/lock.json', 'sources.json']:
    with tempfile.TemporaryDirectory(prefix='acp-codegen-') as directory:
        target = Path(directory)
        docs = target / 'docs/integrations/acp-dotnet-sdk'
        shutil.copytree(root / 'docs/integrations/acp-dotnet-sdk', docs)
        path = docs / relative
        if relative == 'sources.json':
            value = json.loads(path.read_text())
            value['protocol_baseline']['methods'].pop()
            path.write_text(json.dumps(value))
        elif relative == 'schema/lock.json':
            value = json.loads(path.read_text())
            value['schema_sha256'] = '0' * 64
            path.write_text(json.dumps(value))
        else:
            path.write_bytes(path.read_bytes() + b'\n')
        result = run(target)
        generated = target / 'Backend/src/OpenAgent.Runtime.Acp/Protocol/Generated'
        probes.append({'input': relative, 'rejected': result['exitCode'] != 0 and not generated.exists(), **result})
report = {'status': 'PASS' if before == after_first == after_second and first['exitCode'] == second['exitCode'] == 0 and all(p['rejected'] for p in probes) else 'FAIL',
          'fileCount': len(before), 'addedFiles': sorted(set(after_second) - set(before)), 'removedFiles': sorted(set(before) - set(after_second)), 'changedFiles': sorted(k for k in set(before) & set(after_second) if before[k] != after_second[k]), 'before': before, 'afterFirst': after_first, 'afterSecond': after_second,
          'first': first, 'second': second, 'invalidInputProbes': probes}
report_path.write_text(json.dumps(report, indent=2) + '\n')
print(f"{report['status']}: {len(before)} files, two byte-identical runs, {sum(p['rejected'] for p in probes)}/{len(probes)} tamper probes rejected before output")
sys.exit(0 if report['status'] == 'PASS' else 1)
