"""Exercise command isolation without starting Docker containers."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class DeploymentScriptsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.tmp = Path(self.directory.name)
        self.log = self.tmp / 'calls.jsonl'
        docker = self.tmp / 'docker'
        docker.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
with open(os.environ['DOCKER_TEST_LOG'], 'a') as log:
    log.write(json.dumps(args) + '\\n')
if args[:2] == ['image', 'inspect'] and args[2] == os.environ.get('MISSING_IMAGE'):
    sys.exit(1)
if 'config' in args and '--images' in args:
    if args[-2] == '--images':
        app = args[-1]
    else:
        app = pathlib.Path(args[args.index('--file') + 1]).name.split('.')[0]
    print('openagent-' + app + ':latest')
if 'config' in args and '--format' in args:
    print(json.dumps({'networks': {'app': {'external': True, 'name': 'openagent-infrastructure'}}}))
''')
        docker.chmod(0o755)
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith('OPENAGENT_')}
        self.env.update(PATH=str(self.tmp) + os.pathsep + os.environ['PATH'],
                        DOCKER_TEST_LOG=str(self.log))
        self.env_file = self.tmp / 'application.env'
        self.env_file.write_text('')

    def run_script(self, name, *args):
        result = subprocess.run(['bash', str(ROOT / 'scripts' / name), *args],
                                env=self.env, cwd=self.tmp, capture_output=True, text=True)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, calls

    def deploy(self, app):
        return self.run_script('deploy.sh', '--app', app, '--env-file', str(self.env_file))

    def test_build_exports_only_backend_images(self):
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker',
                                       '--tar-dir', str(self.tmp / 'image exports'))
        self.assertEqual(result.returncode, 0, result.stderr)
        builds = [call for call in calls if 'build' in call]
        self.assertEqual(len(builds), 1)
        self.assertEqual(builds[0][-3:], ['engine', 'router', 'runner'])
        self.assertEqual({call[-1] for call in calls if call[0] == 'save'},
                         {'openagent-' + name + ':latest' for name in ('engine', 'router', 'runner')})

    def test_build_can_select_one_application(self):
        result, calls = self.run_script('build-images.sh', '--app', 'router', '--docker-mode', 'docker')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls[-1][-2:], ['build', 'router'])

    def test_deployment_checks_only_selected_application(self):
        result, calls = self.deploy('router')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call for call in calls if call[:2] == ['image', 'inspect']],
                         [['image', 'inspect', 'openagent-router:latest']])
        self.assertEqual(calls[0][-2:], ['config', '--quiet'])
        self.assertEqual(calls[-1][-6:], ['up', '--detach', '--no-build', '--pull', 'never', '--wait'])
        self.assertFalse(any('runner' in str(call) for call in calls))

    def test_missing_image_never_starts_application(self):
        self.env['MISSING_IMAGE'] = 'openagent-router:latest'
        result, calls = self.deploy('router')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any('up' in call for call in calls))

    def test_env_file_is_never_executed(self):
        marker = self.tmp / 'must-not-exist'
        self.env_file.write_text(f"IGNORED=$(touch '{marker}')\n")
        result, _ = self.deploy('router')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(marker.exists())

    def test_minio_waits_for_server_then_runs_one_shot_initializer(self):
        result, calls = self.deploy('minio')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls[-2][-2:], ['--wait', 'minio'])
        self.assertEqual(calls[-1][-7:],
                         ['run', '--rm', '--no-deps', '--no-build', '--pull', 'never', 'minio-init'])

    def test_missing_env_file_fails_before_docker(self):
        result, calls = self.run_script('deploy.sh', '--app', 'runner', '--env-file', str(self.tmp / 'missing'))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [])

    def test_invalid_application_fails_before_docker(self):
        result, calls = self.deploy('unknown')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main()
