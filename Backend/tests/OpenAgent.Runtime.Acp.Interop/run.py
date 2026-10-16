"""Opt-in official SDK subprocess interoperability; no paid model or credentials."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--console', type=Path, required=True)
parser.add_argument('--python', default=sys.executable)
parser.add_argument('--rust', type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parent
console, rust = str(args.console.resolve()), str(args.rust.resolve())
node, python = str(root / 'node/fixture.mjs'), str(root / 'python/fixture.py')
cases = [
    ('typescript-client', ['node', node, 'client', 'dotnet', console, 'agent']),
    ('python-client', [args.python, python, 'client', 'dotnet', console, 'agent']),
    ('rust-client', [rust, 'client', f'dotnet "{console}" agent']),
]
for name, command in [('typescript', ['node', node, 'agent']), ('python', [args.python, python, 'agent']), ('rust', [rust, 'agent'])]:
    cases.append((name + '-agent', ['dotnet', console, 'interop', '--', *command]))
report = []
for name, command in cases:
    try:
        result = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', timeout=45)
        report.append(dict(scenario=name, status='PASS' if result.returncode == 0 else 'FAIL', exitCode=result.returncode, stdout=result.stdout[-4000:], stderr=result.stderr[-4000:]))
    except subprocess.TimeoutExpired:
        report.append(dict(scenario=name, status='FAIL', error='Timeout'))
print(json.dumps(report, ensure_ascii=False, indent=2))
sys.exit(any(row['status'] != 'PASS' for row in report))
