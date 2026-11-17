"""Deployment tests. Set OPENAGENT_TEST_NGINX=1 to include isolated Docker smoke tests."""
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
REAL_DOCKER = shutil.which('docker')


def clean_environment():
    return {key: value for key, value in os.environ.items()
            if not key.startswith(('OPENAGENT_', 'COMPOSE_', 'PREVIEW_'))}


class DeploymentFixture(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.checkout = Path(directory.name) / 'checkout with spaces'
        self.tmp = self.checkout / 'deploy'
        shutil.copytree(ROOT / 'deploy', self.tmp,
                        ignore=shutil.ignore_patterns('.env', 'certs', '*.key', '*.crt'))
        (self.checkout / 'scripts').mkdir()
        for name in ('deploy.sh', 'build-images.sh'):
            shutil.copy2(ROOT / 'scripts' / name, self.checkout / 'scripts' / name)
        for name in ('Engine.Host', 'Router'):
            target = self.checkout / 'Backend/src' / ('OpenAgent.' + name)
            target.mkdir(parents=True)
            shutil.copyfile(ROOT / 'Backend/src' / target.name / 'Dockerfile', target / 'Dockerfile')
        target = self.checkout / 'Frontend/OpenAgent.Chat'
        target.mkdir(parents=True)
        shutil.copyfile(ROOT / 'Frontend/OpenAgent.Chat/Dockerfile', target / 'Dockerfile')
        shutil.copyfile(ROOT / 'Backend/opencode.version', self.checkout / 'Backend/opencode.version')
        subprocess.run([str(self.checkout / 'scripts/deploy.sh'), 'init'], check=True, capture_output=True)
        certs = self.tmp / 'certs'
        certs.mkdir()
        for name in ('tls.crt', 'tls.key'):
            (certs / name).write_text('test fixture, not a certificate')
        self.env_file = self.tmp / 'openagent/.env'
        with (self.tmp / 'openagent/engine/.env').open('a') as stream:
            stream.write("\nConnectionStrings__OpenAgentDatabase='Host=postgres;Port=5432;Database=openagent;Username=openagent;Password=test-postgres-$literal#value'\n"
                         "FileAssets__ObjectStorage__ServiceUrl=http://minio:9000\n"
                         "FileAssets__ObjectStorage__AccessKey=test-access\n"
                         "FileAssets__ObjectStorage__SecretKey='test-s3-$literal#value'\n")
        with (self.tmp / 'nginx/.env').open('a') as stream:
            stream.write(f"\nOPENAGENT_TLS_CERT_DIR='{certs}'\n")

    def config(self, stack, overrides=None):
        output = subprocess.check_output(
            [REAL_DOCKER, 'compose', '--env-file', str(self.tmp / stack / '.env'), '--file',
             str(self.tmp / stack / 'docker-compose.yml'), 'config', '--format', 'json'],
            env={**clean_environment(), **(overrides or {})}, text=True)
        config = json.loads(output)
        for service in config['services'].values():
            # Re-loadable Compose config output escapes literal dollar signs.
            for key, value in service.get('environment', {}).items():
                service['environment'][key] = value.replace('$$', '$') if isinstance(value, str) else value
        return config


class DeploymentScriptTests(DeploymentFixture):
    def setUp(self):
        super().setUp()
        self.log = self.checkout / 'calls.jsonl'
        binary = self.checkout / 'bin'
        binary.mkdir()
        docker = binary / 'docker'
        docker.write_text('''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ['DOCKER_TEST_LOG'], 'a') as stream:
    stream.write(json.dumps(args) + '\\n')
if args[:2] == ['network', 'inspect']:
    if os.environ.get('MISSING_NETWORK'): sys.exit(1)
    print(os.environ.get('NETWORK_OWNER', 'openagent-nginx/openagent'))
if args[0] == 'compose':
    if 'config' in args and os.environ.get('INVALID_CONFIG'): sys.exit(1)
    if 'up' in args and args[args.index('--project-name') + 1] == os.environ.get('FAILED_PROJECT'): sys.exit(1)
''')
        docker.chmod(0o755)
        self.env = {**clean_environment(), 'PATH': str(binary) + os.pathsep + os.environ['PATH'],
                    'DOCKER_TEST_LOG': str(self.log)}

    def run_script(self, name='deploy.sh', *args):
        self.log.unlink(missing_ok=True)
        result = subprocess.run([str(self.checkout / 'scripts' / name), *args],
                                env=self.env, cwd=self.tmp, capture_output=True, text=True)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, calls

    def test_init_is_private_idempotent_and_does_not_use_docker(self):
        original = self.env_file.read_text()
        result, calls = self.run_script('deploy.sh', 'init')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.env_file.read_text(), original)
        self.assertEqual(self.env_file.stat().st_mode & 0o777, 0o600)
        self.assertEqual(calls, [])
        self.assertFalse((self.tmp / '.env').exists())

    def test_default_deploys_nginx_first_with_fixed_project_names(self):
        self.env.update(OPENAGENT_COMPOSE_PROJECT='override-app', OPENAGENT_NGINX_PROJECT='override-nginx',
                        OPENAGENT_NGINX_NETWORK='override-net', COMPOSE_PROJECT_NAME='override-builtin')
        result, calls = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        updates = [call for call in calls if 'up' in call]
        self.assertEqual([call[call.index('--project-name') + 1] for call in updates],
                         ['openagent-nginx', 'openagent-app'])
        self.assertEqual(len([call for call in calls if call[-2:] == ['config', '--quiet']]), 2)
        self.assertTrue(all('--no-build' in call and '--wait' in call for call in updates))
        self.assertFalse(any(call[:2] == ['network', 'create'] for call in calls))

    def test_app_requires_nginx_network_and_never_touches_entrypoint(self):
        self.env['MISSING_NETWORK'] = '1'
        result, calls = self.run_script('deploy.sh', 'app')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Deploy Nginx first', result.stderr)
        self.assertFalse(any('up' in call for call in calls))
        self.env.pop('MISSING_NETWORK')
        result, calls = self.run_script('deploy.sh', 'app')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(any('openagent-nginx' in call for call in calls))

    def test_foreign_network_is_not_adopted_or_deleted(self):
        self.env['NETWORK_OWNER'] = 'old-project/openagent'
        result, calls = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Migrate the existing openagent network', result.stderr)
        self.assertFalse(any('up' in call or call[:2] == ['network', 'rm'] for call in calls))

    def test_nginx_failure_blocks_app_and_app_failure_preserves_entrypoint(self):
        for failed, expected_count in [('openagent-nginx', 1), ('openagent-app', 2)]:
            with self.subTest(failed=failed):
                self.env['FAILED_PROJECT'] = failed
                result, calls = self.run_script()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len([call for call in calls if 'up' in call]), expected_count)
                self.assertFalse(any('down' in call or 'stop' in call for call in calls))

    def test_invalid_config_and_check_only_do_not_update_services(self):
        self.env['INVALID_CONFIG'] = '1'
        result, calls = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any('up' in call for call in calls))
        self.env.pop('INVALID_CONFIG')
        result, calls = self.run_script('deploy.sh', 'check')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(any('up' in call for call in calls))

    def test_application_values_are_not_executed_by_shell(self):
        marker = self.checkout / 'unexpected-shell-execution'
        with (self.tmp / 'openagent/engine/.env').open('a') as stream:
            stream.write(f"\nFileAssets__ObjectStorage__SecretKey=$(touch '{marker}')\n")
        result, calls = self.run_script('deploy.sh', 'app')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(marker.exists())
        self.assertTrue(any('up' in call for call in calls))

    def test_nginx_can_deploy_without_application_env_files(self):
        for relative in ('openagent/.env', 'openagent/engine/.env', 'openagent/router/.env'):
            (self.tmp / relative).unlink()
        result, calls = self.run_script('deploy.sh', 'nginx')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(any('openagent-app' in call for call in calls))

    def test_nginx_certificate_path_is_relative_to_its_compose_directory(self):
        with (self.tmp / 'nginx/.env').open('a') as stream:
            stream.write('\nOPENAGENT_TLS_CERT_DIR=../certs\n')
        result, calls = self.run_script('deploy.sh', 'nginx')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(any('up' in call for call in calls))

    def test_build_uses_fixed_names_and_exports_three_dev_services(self):
        self.env.update(OPENAGENT_ENGINE_IMAGE='override-image', OPENAGENT_ROUTER_IMAGE='override-image',
                        OPENAGENT_CHAT_IMAGE='override-image')
        result, calls = self.run_script('build-images.sh', 'image exports')
        self.assertEqual(result.returncode, 0, result.stderr)
        builds = [call for call in calls if call[0] == 'build']
        self.assertEqual({call[call.index('--tag') + 1] for call in builds},
                         {'openagent-engine:latest', 'openagent-router:latest', 'openagent-chat:latest'})
        for call in builds:
            self.assertTrue((self.checkout / call[call.index('--file') + 1]).is_file())
        self.assertEqual(calls[-1][-3:], ['openagent-engine:latest', 'openagent-router:latest', 'openagent-chat:latest'])
        self.assertTrue(calls[-1][2].endswith('/image exports/openagent-images.tar'))
        engine = next(call for call in builds if 'openagent-engine:latest' in call)
        version = (ROOT / 'Backend/opencode.version').read_text().strip()
        self.assertIn('OPENCODE_VERSION=' + version, engine)


    def test_invalid_opencode_lock_fails_before_build(self):
        (self.checkout / 'Backend/opencode.version').write_text('latest\n')
        result, calls = self.run_script('build-images.sh')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('strict semantic version', result.stderr)
        self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main()
