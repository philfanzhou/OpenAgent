"""Validate Compose paths and shared defaults without starting services."""
import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]


def config(relative, overrides=None):
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('OPENAGENT_', 'COMPOSE_'))}
    env.update({
        'OPENAGENT_RUNNER_API_KEY': 'layout-test-runner-key-32-characters',
        'OPENAGENT_POSTGRES_PASSWORD': 'layout-test-postgres-password',
        'OPENAGENT_S3_ACCESS_KEY': 'layout-test-s3-user',
        'OPENAGENT_S3_SECRET_KEY': 'layout-test-s3-secret',
        'OPENAGENT_KEYCLOAK_ADMIN_USERNAME': 'layout-test-admin',
        'OPENAGENT_KEYCLOAK_ADMIN_PASSWORD': 'layout-test-admin-password',
    })
    env.update(overrides or {})
    output = subprocess.check_output(
        ['docker', 'compose', '--env-file', os.devnull, '-f', str(ROOT / relative),
         '--profile', 'storage', 'config', '--format', 'json'], env=env, text=True)
    return json.loads(output)


class DeploymentLayoutTests(unittest.TestCase):
    def test_public_addresses_are_shared(self):
        for overrides, host, port, origin in [
            ({}, 'localhost', '58081', 'https://localhost:8081'),
            ({'OPENAGENT_PUBLIC_HOST': 'review.example', 'OPENAGENT_KEYCLOAK_PORT': '58443',
              'OPENAGENT_CHAT_PORT': '443', 'OPENAGENT_CHAT_PUBLIC_URL': 'https://review.example'},
             'review.example', '58443', 'https://review.example')
        ]:
            with self.subTest(overrides=overrides):
                infra = config('deploy/infrastructure/docker-compose.yml', overrides)
                app = config('deploy/openagent/docker-compose.yml', overrides)
                kc = infra['services']['keycloak']
                self.assertEqual(kc['environment']['KC_HOSTNAME'], f'https://{host}:{port}')
                self.assertEqual(kc['environment']['OPENAGENT_CHAT_PUBLIC_URL'], origin)
                for name in ('engine', 'router'):
                    env = app['services'][name]['environment']
                    self.assertEqual(env['Authentication__Authority'], f'https://{host}:{port}/realms/openagent')
                    self.assertEqual(env['Cors__AllowedOrigins__0'], origin)
                self.assertFalse(any(p['target'] == 8080 for p in kc['ports']))

    def test_mounts_network_and_preview_context(self):
        infra = config('deploy/infrastructure/docker-compose.yml')
        app = config('deploy/openagent/docker-compose.yml')
        self.assertEqual(infra['name'], 'openagent-infrastructure')
        self.assertEqual(app['name'], 'openagent-app')
        self.assertEqual(infra['networks']['default']['name'], app['networks']['infrastructure']['name'])
        for document in (infra, app):
            for service in document['services'].values():
                for mount in service.get('volumes', []):
                    if mount['type'] == 'bind':
                        self.assertTrue(Path(mount['source']).exists(), mount['source'])
        preview = config('deploy/openagent/preview.compose.yml')
        for service in preview['services'].values():
            self.assertEqual(Path(service['build']['context']), ROOT)

    def test_storage_security_and_internal_ports(self):
        app = config('deploy/openagent/docker-compose.yml',
                     {'OPENAGENT_POSTGRES_PORT': '55432', 'OPENAGENT_REDIS_PORT': '56379'})
        env = app['services']['engine']['environment']
        self.assertIn('Host=postgres;Port=5432;', env['ConnectionStrings__OpenAgentDatabase'])
        self.assertEqual(env['ConnectionStrings__Redis'], 'redis:6379')
        self.assertEqual(env['OPENAGENT_S3_ALLOW_INSECURE_TLS'], 'false')
        self.assertNotIn('FileAssets__ObjectStorage__PublicServiceUrl', env)
        self.assertEqual(env['FileAssets__ObjectStorage__ServiceUrl'], 'http://minio:9000')


if __name__ == '__main__':
    unittest.main()
