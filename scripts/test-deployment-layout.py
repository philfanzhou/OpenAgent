"""Validate independent deployments without starting services or contacting S3."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
ENGINE_REQUIRED = {
    'ConnectionStrings__OpenAgentDatabase': 'Host=db.example;Database=agents;Username=agents;Password=test',
    'FileAssets__ObjectStorage__ServiceUrl': 'https://s3.example',
    'FileAssets__ObjectStorage__AccessKey': 'example-access',
    'FileAssets__ObjectStorage__SecretKey': 'example-secret',
}


def render(relative, overrides=None, check=True):
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('OPENAGENT_', 'COMPOSE_', 'Authentication__',
                                  'ConnectionStrings__', 'FileAssets__', 'Runner__',
                                  'POSTGRES_', 'MINIO_', 'KC_'))}
    env.update(overrides or {})
    return subprocess.run(
        ['docker', 'compose', '--env-file', os.devnull, '-f', str(ROOT / relative),
         'config', '--format', 'json'], env=env, text=True,
        capture_output=True, check=check)


def config(app, overrides=None, directory='openagent'):
    return json.loads(render(f'deploy/{directory}/{app}.compose.yml', overrides).stdout)


class DeploymentLayoutTests(unittest.TestCase):
    def test_router_is_independent_of_database_s3_runner_and_keycloak(self):
        document = config('router')
        self.assertEqual(set(document['services']), {'router'})
        router = document['services']['router']
        self.assertNotIn('depends_on', router)
        env = router['environment']
        self.assertEqual(env['Authentication__Mode'], 'Basic')
        self.assertEqual(env['Authentication__EnableKeycloak'], 'false')
        self.assertEqual(env['Authentication__RequireHttpsMetadata'], 'true')
        self.assertEqual(env['ConnectionStrings__Redis'], '')
        self.assertFalse(any('ObjectStorage' in key or 'OpenAgentDatabase' in key for key in env))

    def test_engine_requires_each_database_and_s3_value(self):
        for key in ENGINE_REQUIRED:
            with self.subTest(key=key):
                settings = {k: v for k, v in ENGINE_REQUIRED.items() if k != key}
                result = render('deploy/openagent/engine.compose.yml', settings, check=False)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(key, result.stderr)

    def test_engine_accepts_external_s3_and_region_and_addressing(self):
        settings = dict(ENGINE_REQUIRED,
                        FileAssets__ObjectStorage__Region='custom-region',
                        FileAssets__ObjectStorage__ForcePathStyle='true',
                        FileAssets__ObjectStorage__BucketName='existing-bucket')
        document = config('engine', settings)
        engine = document['services']['engine']
        self.assertNotIn('depends_on', engine)
        env = engine['environment']
        self.assertEqual(env['FileAssets__ObjectStorage__ServiceUrl'], 'https://s3.example')
        self.assertEqual(env['FileAssets__ObjectStorage__Region'], 'custom-region')
        self.assertEqual(env['FileAssets__ObjectStorage__ForcePathStyle'], 'true')
        self.assertEqual(env['FileAssets__ObjectStorage__BucketName'], 'existing-bucket')
        self.assertEqual(env['FileAssets__Enabled'], 'true')
        self.assertEqual(env['CodeExecution__Enabled'], 'false')
        self.assertEqual(env['Authentication__Mode'], 'Basic')

    def test_runner_requires_only_its_own_key(self):
        document = config('runner', {'Runner__ApiKey': 'a' * 32})
        self.assertEqual(set(document['services']), {'runner'})
        self.assertEqual(document['services']['runner']['environment']['Runner__ApiKey'], 'a' * 32)
        self.assertTrue(document['services']['runner']['privileged'])
        result = render('deploy/openagent/runner.compose.yml', check=False)
        self.assertNotEqual(result.returncode, 0)

    def test_chat_receives_no_backend_secrets(self):
        chat = config('chat')['services']['chat']
        self.assertNotIn('environment', chat)
        self.assertNotIn('build', chat)
        self.assertNotIn('depends_on', chat)

    def test_combined_stack_keeps_application_configuration_separate(self):
        with tempfile.TemporaryDirectory() as directory:
            settings = {}
            values = {
                'engine': dict(ENGINE_REQUIRED, Authentication__BasicPassword='engine-password'),
                'router': {'Authentication__BasicPassword': 'router-password'},
                'runner': {'Runner__ApiKey': 'a' * 32},
                'chat': {},
            }
            for app, app_values in values.items():
                path = Path(directory) / f'{app}.env'
                path.write_text('\n'.join(f"{k}='{v}'" for k, v in app_values.items()))
                settings[f'OPENAGENT_{app.upper()}_ENV_FILE'] = str(path)
            document = json.loads(render('deploy/openagent/docker-compose.yml', settings).stdout)
            self.assertEqual(set(document['services']), set(values))
            self.assertEqual(document['services']['engine']['environment']['Authentication__BasicPassword'], 'engine-password')
            self.assertEqual(document['services']['router']['environment']['Authentication__BasicPassword'], 'router-password')
            self.assertNotIn('keycloak', document['services'])

    def test_infrastructure_starts_independently(self):
        cases = [('postgres', {'POSTGRES_PASSWORD': 'example'}), ('redis', {}),
                 ('minio', {'MINIO_ROOT_USER': 'example', 'MINIO_ROOT_PASSWORD': 'example-secret'})]
        for app, settings in cases:
            with self.subTest(app=app):
                document = config(app, settings, 'infrastructure')
                self.assertNotIn('keycloak', document['services'])
                self.assertIn(app, document['services'])
        document = config('minio', cases[2][1], 'infrastructure')
        self.assertIn('$$S3_BUCKET', document['services']['minio-init']['entrypoint'][-1])

    def test_build_contexts_match_split_repository(self):
        document = json.loads(render('deploy/build.compose.yml').stdout)
        self.assertEqual(set(document['services']), {'engine', 'router', 'runner'})
        for app, service in document['services'].items():
            context = Path(service['build']['context'])
            self.assertEqual(context, ROOT)
            self.assertTrue((context / service['build']['dockerfile']).is_file(), app)


if __name__ == '__main__':
    unittest.main()
