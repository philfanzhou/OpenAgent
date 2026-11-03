"""Validate Compose paths and shared defaults without starting services."""
import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]


def config(relative, overrides=None, profiles=("storage", "keycloak")):
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('OPENAGENT_', 'COMPOSE_'))}
    env.update({
        'OPENAGENT_RUNNER_API_KEY': 'layout-test-runner-key-32-characters',
        'OPENAGENT_POSTGRES_PASSWORD': 'layout-database-password',
        'OPENAGENT_AUTH_BASIC_PASSWORD': 'layout-basic-password',
        'OPENAGENT_S3_SERVICE_URL': 'http://minio:9000',
        'OPENAGENT_S3_ACCESS_KEY': 'layout-access-key',
        'OPENAGENT_S3_SECRET_KEY': 'layout-secret-key',
    })
    env.update(overrides or {})
    profile_args = [arg for profile in profiles for arg in ('--profile', profile)]
    output = subprocess.check_output(
        ['docker', 'compose', '--env-file', os.devnull, '-f', str(ROOT / relative),
         *profile_args, 'config', '--format', 'json'], env=env, text=True, stderr=subprocess.PIPE)
    return json.loads(output)


class DeploymentLayoutTests(unittest.TestCase):
    def test_public_addresses_are_shared(self):
        infra = config('deploy/infrastructure/docker-compose.yml')
        app = config('deploy/openagent/docker-compose.yml')
        kc = infra['services']['keycloak']
        self.assertEqual(kc['environment']['KC_HOSTNAME'], 'https://localhost:58081')
        self.assertEqual(kc['environment']['OPENAGENT_CHAT_PUBLIC_URL'], 'https://localhost:8081')
        for name in ('engine', 'router'):
            env = app['services'][name]['environment']
            self.assertEqual(env['Cors__AllowedOrigins__0'], 'https://localhost:8081')
            self.assertEqual(env['Authentication__Mode'], 'Basic')
        self.assertFalse(any(p['target'] == 8080 for p in kc['ports']))

    def test_mounts_network_and_preview_context(self):
        infra = config('deploy/infrastructure/docker-compose.yml')
        app = config('deploy/openagent/docker-compose.yml')
        self.assertEqual(infra['name'], 'openagent-infrastructure')
        self.assertEqual(app['name'], 'openagent-app')
        self.assertEqual(app['networks']['openagent']['name'], 'openagent')
        self.assertEqual(infra['networks']['default']['name'], app['networks']['infrastructure']['name'])
        self.assertEqual(app['services']['engine']['networks'],
                         {'openagent': None, 'infrastructure': None})
        self.assertEqual(app['services']['router']['networks'],
                         {'openagent': None, 'infrastructure': None})
        for name in ('runner', 'chat', 'nginx'):
            self.assertEqual(app['services'][name]['networks'], {'openagent': None})
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
        self.assertNotIn('ConnectionStrings__Redis', env)
        self.assertNotIn('OPENAGENT_ALLOW_INSECURE_TLS', env)
        self.assertNotIn('OPENAGENT_S3_ALLOW_INSECURE_TLS', env)
        self.assertNotIn('FileAssets__ObjectStorage__PublicServiceUrl', env)
        self.assertEqual(env['FileAssets__ObjectStorage__ServiceUrl'], 'http://minio:9000')

    def test_basic_defaults_and_optional_keycloak(self):
        app = config('deploy/openagent/docker-compose.yml')
        for service in ('engine', 'router'):
            env = app['services'][service]['environment']
            self.assertEqual(env['Authentication__Mode'], 'Basic')
            self.assertEqual(env['Authentication__EnableKeycloak'], 'false')
            self.assertEqual(env['Authentication__BasicUsername'], 'admin')
            self.assertEqual(env['Authentication__BasicTenantId'], 'development')
            self.assertEqual(env['Authentication__RequireHttpsMetadata'], 'true')
        infra = config('deploy/infrastructure/docker-compose.yml', profiles=('storage',))
        self.assertNotIn('keycloak', infra['services'])

    def test_database_and_s3_are_required(self):
        for key in ('OPENAGENT_POSTGRES_PASSWORD', 'OPENAGENT_S3_SERVICE_URL',
                    'OPENAGENT_S3_ACCESS_KEY', 'OPENAGENT_S3_SECRET_KEY',
                    'OPENAGENT_AUTH_BASIC_PASSWORD', 'OPENAGENT_RUNNER_API_KEY'):
            with self.subTest(key=key):
                with self.assertRaises(subprocess.CalledProcessError) as error:
                    config('deploy/openagent/docker-compose.yml', {key: ''})
                self.assertIn(key, error.exception.stderr)

    def test_external_s3_and_shared_basic_credentials(self):
        app = config('deploy/openagent/docker-compose.yml', {
            'OPENAGENT_S3_SERVICE_URL': 'https://objects.example.com',
            'OPENAGENT_AUTH_BASIC_PASSWORD': 'configured-password',
        })
        engine = app['services']['engine']['environment']
        self.assertEqual(engine['FileAssets__ObjectStorage__ServiceUrl'], 'https://objects.example.com')
        self.assertEqual(engine['FileAssets__ObjectStorage__ForcePathStyle'], 'true')
        for service in ('engine', 'router'):
            self.assertEqual(app['services'][service]['environment']['Authentication__BasicPassword'],
                             'configured-password')
        for service in ('router', 'runner', 'chat'):
            env = app['services'][service].get('environment', {})
            self.assertFalse(any(key.startswith('FileAssets__ObjectStorage__')
                                 or key == 'ConnectionStrings__OpenAgentDatabase' for key in env))


if __name__ == '__main__':
    unittest.main()
