"""Compose layout checks; OPENAGENT_TEST_NGINX=1 additionally runs isolated Docker checks."""
import os
from pathlib import Path
import runpy
import socket
import ssl
import subprocess
import time
import unittest
import urllib.error
import urllib.request
import uuid

_helpers = runpy.run_path(str(Path(__file__).with_name('test-deployment-scripts.py')))
DeploymentFixture = _helpers['DeploymentFixture']
REAL_DOCKER = _helpers['REAL_DOCKER']
clean_environment = _helpers['clean_environment']


@unittest.skipUnless(REAL_DOCKER, 'requires Docker Compose CLI')
class DeploymentLayoutTests(DeploymentFixture):
    def test_names_and_images_ignore_old_environment_overrides(self):
        overrides = {name: 'ignored-override' for name in (
            'OPENAGENT_COMPOSE_PROJECT', 'OPENAGENT_NGINX_PROJECT', 'OPENAGENT_NGINX_NETWORK',
            'OPENAGENT_APP_NETWORK', 'OPENAGENT_INFRA_NETWORK', 'OPENAGENT_ENGINE_IMAGE',
            'OPENAGENT_ROUTER_IMAGE', 'OPENAGENT_CHAT_IMAGE', 'OPENAGENT_NGINX_IMAGE')}
        app, nginx = self.config('openagent', overrides), self.config('nginx', overrides)
        self.assertEqual((app['name'], nginx['name']), ('openagent-app', 'openagent-nginx'))
        self.assertEqual(app['networks']['openagent']['name'], 'openagent')
        self.assertEqual(nginx['networks']['openagent']['name'], 'openagent')
        self.assertEqual(app['networks']['infrastructure']['name'], 'openagent-infrastructure')
        for name in ('engine', 'router', 'chat'):
            self.assertEqual(app['services'][name]['image'], 'openagent-' + name + ':latest')
        self.assertEqual(nginx['services']['nginx']['image'], 'nginx:latest')

    def test_shared_configuration_and_secret_scoping(self):
        services = self.config('openagent')['services']
        engine, router = [services[name]['environment'] for name in ('engine', 'router')]
        self.assertEqual(set(services), {'engine', 'router', 'chat'})
        for key in ('ASPNETCORE_ENVIRONMENT', 'ConnectionStrings__Redis', 'Authentication__Authority',
                    'Authentication__Audience', 'Authentication__ClientId'):
            self.assertEqual(engine[key], router[key])
        self.assertIn('Password=test-postgres-$literal#value', engine['ConnectionStrings__OpenAgentDatabase'])
        self.assertEqual(engine['Authentication__Mode'], 'JwtBearer')
        self.assertEqual(engine['Authentication__EnableKeycloak'], 'true')
        self.assertEqual(engine['Authentication__MetadataAddress'], router['Authentication__MetadataAddress'])
        self.assertTrue(engine['Authentication__MetadataAddress'].startswith('https://'))
        self.assertNotIn('Cors__AllowedOrigins__0', engine)
        self.assertNotIn('ConnectionStrings__OpenAgentDatabase', router)
        self.assertNotIn('FileAssets__ObjectStorage__SecretKey', router)
        self.assertFalse(services['chat'].get('environment'))
        self.assertFalse(any('SECRET' in key or 'PASSWORD' in key for key in self.config('nginx')['services']['nginx']['environment']))

    def test_runtime_overrides_remain_supported(self):
        with (self.tmp / 'openagent/.env').open('a') as stream:
            stream.write('\nOpenTelemetry__OtlpEndpoint=http://collector:4317\nCors__AllowedOrigins__0=https://review.example\nAuthentication__Authority=https://sso.example/realms/openagent\n')
        with (self.tmp / 'openagent/engine/.env').open('a') as stream:
            stream.write('\nOpenTelemetry__ServiceName=custom-engine\n')
        app = self.config('openagent')
        for name in ('engine', 'router'):
            env = app['services'][name]['environment']
            self.assertEqual(env['Cors__AllowedOrigins__0'], 'https://review.example')
            self.assertEqual(env['Authentication__Authority'], 'https://sso.example/realms/openagent')
            self.assertEqual(env['OpenTelemetry__OtlpEndpoint'], 'http://collector:4317')
        self.assertNotIn('OpenTelemetry__ServiceName', app['services']['router']['environment'])

    def test_network_owner_and_data_volume_are_stable(self):
        app, nginx = self.config('openagent'), self.config('nginx')
        self.assertTrue(app['networks']['openagent']['external'])
        self.assertFalse(nginx['networks']['openagent'].get('external', False))
        self.assertEqual(app['volumes']['engine-data-protection']['name'], 'openagent-app_engine-data-protection')
        mount = app['services']['engine']['volumes'][0]
        self.assertEqual(mount['target'], '/home/app/.aspnet/DataProtection-Keys')
        nginx_service = nginx['services']['nginx']
        self.assertEqual(nginx_service['volumes'][1]['source'], str(self.tmp / 'nginx/certs'))
        changed = self.config('nginx', {'OPENAGENT_CHAT_PORT': '18081', 'OPENAGENT_ROUTER_PORT': '18082',
                                         'OPENAGENT_ENGINE_PORT': '18083'})['services']['nginx']
        self.assertEqual({(port['target'], str(port['published'])) for port in changed['ports']},
                         {(8081, '18081'), (8082, '18082'), (8083, '18083')})
        self.assertNotIn('depends_on', nginx_service)
        self.assertFalse(any(service.get('ports') for service in app['services'].values()))

    def test_storage_values_are_configured_directly_without_aliases(self):
        with (self.tmp / 'openagent/engine/.env').open('a') as stream:
            stream.write('\nFileAssets__ObjectStorage__ServiceUrl=https://objects.example\n')
        app = self.config('openagent', {'OPENAGENT_S3_SERVICE_URL': 'https://ignored.example'})
        self.assertEqual(app['services']['engine']['environment']['FileAssets__ObjectStorage__ServiceUrl'],
                         'https://objects.example')
        self.assertNotIn('OPENAGENT_S3_SERVICE_URL', app['services']['engine']['environment'])

    def test_audit_and_intent_credentials_remain_scoped(self):
        with (self.tmp / 'openagent/.env').open('a') as stream:
            stream.write('\nOTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://audit.example\n'
                         'OTEL_EXPORTER_OTLP_TRACES_HEADERS=Authorization=Bearer-test-token\n'
                         'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf\n')
        with (self.tmp / 'openagent/router/.env').open('a') as stream:
            stream.write('\nOPENAGENT_INTERN_DECISION_API_KEY=intent-test-token\n')
        app = self.config('openagent')['services']
        for name in ('engine', 'router'):
            self.assertEqual(app[name]['environment']['OTEL_EXPORTER_OTLP_TRACES_ENDPOINT'], 'https://audit.example')
            self.assertEqual(app[name]['environment']['OTEL_EXPORTER_OTLP_PROTOCOL'], 'http/protobuf')
        self.assertNotIn('OPENAGENT_INTERN_DECISION_API_KEY', app['engine']['environment'])
        self.assertEqual(app['router']['environment']['OPENAGENT_INTERN_DECISION_API_KEY'], 'intent-test-token')

    def test_basic_configuration_can_be_shared_explicitly(self):
        with (self.tmp / 'openagent/.env').open('a') as stream:
            stream.write('\nAuthentication__Mode=Basic\nAuthentication__EnableKeycloak=false\n'
                         'Authentication__BasicPassword=test-basic-password\n')
        for service in self.config('openagent')['services'].values():
            env = service.get('environment', {})
            if env:
                self.assertEqual(env['Authentication__Mode'], 'Basic')
                self.assertEqual(env['Authentication__BasicPassword'], 'test-basic-password')


BACKEND = '''import http.server, os, threading, time
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream' if self.path == '/stream' else 'text/plain')
        self.end_headers()
        if self.path == '/stream':
            self.wfile.write(b'data: first\\n\\n')
            self.wfile.flush()
            time.sleep(2)
            self.wfile.write(b'data: second\\n\\n')
        else:
            self.wfile.write(os.environ['RESPONSE'].encode())
    def log_message(self, *args): pass
for port in (80, 5001, 5208):
    threading.Thread(target=http.server.ThreadingHTTPServer(('0.0.0.0', port), Handler).serve_forever, daemon=True).start()
threading.Event().wait()
'''


@unittest.skipUnless(os.environ.get('OPENAGENT_TEST_NGINX') == '1', 'set OPENAGENT_TEST_NGINX=1 for Docker smoke tests')
class NginxDeploymentTests(DeploymentFixture):
    def docker(self, *args):
        return subprocess.check_output([REAL_DOCKER, *args], text=True, stderr=subprocess.STDOUT).strip()

    def setUp(self):
        super().setUp()
        self.env_file = self.tmp / 'nginx/.env'
        self.prefix = 'openagent-nginx-test-' + uuid.uuid4().hex[:10]
        self.network = self.prefix
        self.containers = []
        self.addCleanup(self.cleanup_docker)
        # Namespace only the disposable fixture; production names remain fixed.
        compose = self.tmp / 'nginx/docker-compose.yml'
        compose.write_text(compose.read_text().replace('name: openagent-nginx', 'name: ' + self.prefix)
                           .replace('    name: openagent\n', '    name: ' + self.network + '\n'))
        sockets = [socket.socket() for _ in range(3)]
        try:
            for item in sockets:
                item.bind(('127.0.0.1', 0))
            self.ports = [item.getsockname()[1] for item in sockets]
        finally:
            for item in sockets:
                item.close()
        with self.env_file.open('a') as env_file:
            for name, port in zip(('CHAT', 'ROUTER', 'ENGINE'), self.ports):
                env_file.write(f'\nOPENAGENT_{name}_PORT={port}\n')
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                        '-subj', '/CN=localhost', '-keyout', str(self.tmp / 'nginx/certs/tls.key'),
                        '-out', str(self.tmp / 'nginx/certs/tls.crt')], check=True, capture_output=True)
        self.docker('compose', '--project-name', self.prefix, '--env-file', str(self.env_file),
                    '--file', str(compose), 'up', '--detach', '--pull', 'never', '--wait')
        self.nginx = self.prefix + '-nginx-1'
        self.containers.append(self.nginx)
        self.context = ssl._create_unverified_context()

    def cleanup_docker(self):
        # Query the isolated project as well, so a partially failed startup is cleaned up.
        names = list(self.containers)
        names.extend(self.docker('ps', '-aq', '--filter', f'label=com.docker.compose.project={self.prefix}').split())
        if names:
            subprocess.run(['docker', 'rm', '-f', *names], capture_output=True)
        subprocess.run(['docker', 'network', 'rm', self.network], capture_output=True)

    def request(self, port, path='/'):
        return urllib.request.urlopen(f'https://127.0.0.1:{port}{path}', context=self.context, timeout=4)

    def wait_for_body(self, expected):
        deadline = time.monotonic() + 30
        last_error = None
        while time.monotonic() < deadline:
            try:
                for port in self.ports:
                    with self.request(port) as response:
                        if response.read().decode() != expected:
                            break
                else:
                    return
            except (OSError, urllib.error.URLError) as error:
                last_error = str(error)
                if isinstance(error, urllib.error.HTTPError):
                    error.close()
            time.sleep(0.25)
        self.fail(f'Proxy did not recover: {last_error}')

    def start_backend(self, response):
        name = self.prefix + '-backend'
        self.docker('run', '-d', '--name', name, '--network', self.network,
                    '--network-alias', 'chat', '--network-alias', 'router', '--network-alias', 'engine',
                    '-e', 'RESPONSE=' + response, 'python:3.12-alpine', 'python', '-u', '-c', BACKEND)
        if name not in self.containers:
            self.containers.append(name)
        return name

    def test_missing_upstreams_recreation_and_streaming(self):
        self.assertEqual(self.docker('inspect', '-f', '{{.State.Running}}', self.nginx), 'true')
        for port in self.ports:
            with self.assertRaises(urllib.error.HTTPError) as result:
                self.request(port)
            self.assertEqual(result.exception.code, 502)
            result.exception.close()
        backend = self.start_backend('first-instance')
        self.wait_for_body('first-instance')
        # Verify streaming on the Router entry, not just a buffered completed response.
        started = time.monotonic()
        with self.request(self.ports[1], '/stream') as stream:
            self.assertEqual(stream.readline(), b'data: first\n')
            self.assertLess(time.monotonic() - started, 1.5)
            self.assertIn(b'data: second', stream.read())
        original_ip = self.docker('inspect', '-f', '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}', backend)
        self.docker('rm', '-f', backend)
        # Occupy the old address, ensuring the replacement backend has a different IP.
        blocker = self.prefix + '-blocker'
        self.containers.append(blocker)
        self.docker('run', '-d', '--name', blocker, '--network', self.network,
                    '--ip', original_ip, 'alpine:3', 'sleep', '60')
        self.start_backend('replacement-instance')
        replacement_ip = self.docker('inspect', '-f', '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}', backend)
        self.assertNotEqual(original_ip, replacement_ip)
        self.wait_for_body('replacement-instance')
        self.assertEqual(self.docker('inspect', '-f', '{{.RestartCount}}', self.nginx), '0')

    def test_nginx_owns_network_and_application_down_preserves_it(self):
        owner_format = '{{index .Labels "com.docker.compose.project"}}/{{index .Labels "com.docker.compose.network"}}'
        self.assertEqual(self.docker('network', 'inspect', self.network, '--format', owner_format),
                         self.prefix + '/openagent')
        compose = self.tmp / 'application.compose.yml'
        compose.write_text(f"""services:
  probe:
    image: alpine:3
    command: [sleep, '60']
    networks: [entrypoint]
networks:
  entrypoint:
    external: true
    name: {self.network}
""")
        project = self.prefix + '-app'
        self.containers.append(project + '-probe-1')
        args = ('compose', '--project-name', project, '--file', str(compose))
        self.docker(*args, 'up', '--detach', '--pull', 'never')
        self.docker(*args, 'down')
        self.assertEqual(self.docker('network', 'inspect', self.network, '--format', owner_format),
                         self.prefix + '/openagent')
        self.assertEqual(self.docker('inspect', '-f', '{{.State.Running}}', self.nginx), 'true')

    def test_env_file_preserves_literal_dollar_signs_at_runtime(self):
        # config --format json escapes dollars; the actual container value must not double them.
        self.addCleanup(subprocess.run, ['docker', 'network', 'rm', self.prefix + '-literal_default'], capture_output=True)
        compose = self.tmp / 'literal.compose.yml'
        compose.write_text('''services:
  probe:
    image: alpine:3
    env_file:
      - ./openagent/engine/.env
    command: [printenv, FileAssets__ObjectStorage__SecretKey]
''')
        output = self.docker('compose', '--project-name', self.prefix + '-literal', '--env-file',
                             str(self.env_file), '--file', str(compose), 'run', '--rm', '--no-deps',
                             'probe')
        self.assertIn('test-s3-$literal#value', output)
        self.assertNotIn('test-s3-$$literal', output)
        self.docker('compose', '--project-name', self.prefix + '-literal', '--env-file',
                    str(self.env_file), '--file', str(compose), 'down')


if __name__ == '__main__':
    unittest.main()
