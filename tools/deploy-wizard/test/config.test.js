import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, cpSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import net from 'node:net';
import http from 'node:http';
import { FILES, FIELDS, buildEnvs, defaultForm, postgresConnection, redisConnection, collectProbePorts } from '../public/config.js';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const complete = overrides => ({ ...defaultForm(), pgPassword: 'pg-pass', s3AccessKey: 'access', s3SecretKey: 'secret', ...overrides });
const envMap = text => Object.fromEntries(text.split('\n').filter(l => l && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]));
const outputMap = form => Object.fromEntries(buildEnvs(form).files.map(f => [f.id, envMap(f.text)]));

test('default deployment matches all active dev example keys and scopes secrets', () => {
  const { files, errors } = buildEnvs(complete());
  assert.deepEqual(errors, []);
  for (const file of files) {
    const example = envMap(readFileSync(join(root, `${file.path}.example`), 'utf8'));
    assert.deepEqual(Object.keys(envMap(file.text)).sort(), Object.keys(example).sort(), file.path);
    for (const [key, value] of Object.entries(example)) {
      if (key.startsWith('ConnectionStrings__') || ['FileAssets__ObjectStorage__AccessKey', 'FileAssets__ObjectStorage__SecretKey'].includes(key)) continue;
      assert.equal(envMap(file.text)[key], value, key);
    }
  }
  const map = outputMap(complete());
  assert.ok(map.engine.ConnectionStrings__OpenAgentDatabase);
  for (const id of ['shared', 'router', 'nginx']) {
    assert.equal(map[id].ConnectionStrings__OpenAgentDatabase, undefined);
    assert.equal(map[id].FileAssets__ObjectStorage__SecretKey, undefined);
  }
  assert.equal(map.shared.OPENAGENT_POSTGRES_PASSWORD, undefined);
  assert.equal(new Set(FIELDS.map(f => f.key)).size, FIELDS.length);
});

test('PostgreSQL components preserve delimiters, quotes and password whitespace', () => {
  const form = complete({ pgHost: 'db.internal', pgPort: '6432', pgDatabase: 'a;b', pgUser: 'a"b', pgPassword: ' x;"\'=$#\\ ', pgSslMode: 'VerifyFull' });
  assert.equal(postgresConnection(form), 'Host=db.internal;Port=6432;Database="a;b";Username="a""b";Password=" x;""\'=$#\\ ";SSL Mode=VerifyFull');
  assert.deepEqual(buildEnvs(form).errors, []);
});

test('Redis components support ACL, TLS, database and IPv6', () => {
  assert.equal(redisConnection(complete()), 'redis:6379');
  assert.equal(redisConnection(complete({ redisHost: '::1', redisPort: '6380', redisUser: 'agent', redisPassword: 's=$#', redisTls: true, redisDatabase: '2' })), '[::1]:6380,user=agent,password=s=$#,defaultDatabase=2,ssl=true');
});

test('Basic mode switches fields and requires password without exporting JWT settings', () => {
  const form = complete({ authMode: 'Basic' });
  assert.ok(buildEnvs(form).errors.some(e => e.key === 'basicPassword'));
  form.basicPassword = ' secret ';
  const map = outputMap(form).shared;
  assert.equal(map.Authentication__Mode, 'Basic');
  assert.equal(map.Authentication__EnableKeycloak, 'false');
  assert.equal(map.Authentication__Authority, undefined);
  assert.equal(map.Authentication__MetadataAddress, undefined);
  assert.equal(map.Authentication__BasicPassword, "' secret '");
  assert.deepEqual(buildEnvs(form).errors, []);
});

test('disabled storage excludes credentials, probe overrides never enter exports', () => {
  const form = complete({ fileAssetsEnabled: false, pgProbeHost: 'probe-only', redisProbeHost: 'redis-probe-only', s3ProbeUrl: 'https://probe-only.test' });
  const { files, errors } = buildEnvs(form);
  assert.deepEqual(errors, []);
  assert.equal(outputMap(form).engine.FileAssets__Enabled, 'false');
  assert.equal(outputMap(form).engine.FileAssets__ObjectStorage__SecretKey, undefined);
  assert.ok(files.every(f => !f.text.includes('probe-only')));
  assert.deepEqual(collectProbePorts(form), [8081, 8082, 8083]);
});

for (const [key, value] of [
  ['pgPassword', ''], ['pgSslMode', 'invalid'], ['redisHost', 'redis:6379'], ['certDir', "./it's"], ['maxBodySize', 'large'], ['pgPort', '65536'], ['redisDatabase', '-1'], ['redisDatabase', '1.5'],
  ['redisUser', 'a,b'], ['redisPassword', ' password '], ['redisPassword', 'a,b'],
  ['pgPassword', 'x\nINJECTED=true'], ['s3AccessKey', 'x\rINJECTED=true'],
  ['authority', 'http://issuer.test/realm'], ['s3ServiceUrl', 'file:///etc/passwd'], ['enginePort', '8081'],
]) test(`invalid ${key} is rejected: ${JSON.stringify(value)}`, () => {
  const result = buildEnvs(complete({ [key]: value }));
  assert.ok(result.errors.some(e => e.key === key || (key === 'enginePort' && e.key === 'chatPort')));
  assert.ok(result.files.every(f => !f.text.includes('\nINJECTED=')));
});

const hasCompose = spawnSync('docker', ['compose', 'version'], { stdio: 'ignore' }).status === 0;
test('real Compose parses four files, preserves literal credentials and keeps service ownership', { skip: !hasCompose }, () => {
  const dir = mkdtempSync(join(tmpdir(), 'deploy-wizard-'));
  try {
    for (const name of ['openagent', 'nginx']) {
      mkdirSync(join(dir, 'deploy', name), { recursive: true });
      cpSync(join(root, 'deploy', name, 'docker-compose.yml'), join(dir, 'deploy', name, 'docker-compose.yml'));
    }
    const secret = `  p;$literal#'"\\tail  `;
    const form = complete({ pgPassword: secret, redisPassword: `p=$literal#'"\\tail`, s3SecretKey: secret, certDir: './certs with spaces', otlpHeaders: 'Authorization=Bearer $token#literal', tracesHeaders: 'Authorization=Bearer $trace#literal', intentKey: secret });
    const generated = buildEnvs(form);
    assert.deepEqual(generated.errors, []);
    for (const file of generated.files) {
      mkdirSync(resolve(dir, file.path, '..'), { recursive: true });
      writeFileSync(join(dir, file.path), file.text);
    }
    const config = name => {
      const result = spawnSync('docker', ['compose', '--env-file', join(dir, 'deploy', name, '.env'), '-f', join(dir, 'deploy', name, 'docker-compose.yml'), 'config', '--format', 'json'], { encoding: 'utf8', env: { PATH: process.env.PATH, HOME: process.env.HOME } });
      assert.equal(result.status, 0, result.stderr);
      // Compose config escapes literal $ as $$ so its output can be parsed again.
      return JSON.parse(result.stdout.replaceAll('$$', '$'));
    };
    const { services } = config('openagent');
    const engine = services.engine.environment;
    const router = services.router.environment;
    assert.equal(engine.ConnectionStrings__OpenAgentDatabase, postgresConnection(form));
    assert.equal(engine.ConnectionStrings__Redis, redisConnection(form));
    assert.equal(router.ConnectionStrings__Redis, redisConnection(form));
    assert.equal(engine.FileAssets__ObjectStorage__SecretKey, secret);
    assert.equal(router.FileAssets__ObjectStorage__SecretKey, undefined);
    assert.equal(router.ConnectionStrings__OpenAgentDatabase, undefined);
    assert.equal(router.OPENAGENT_INTERN_DECISION_API_KEY, secret);
    assert.equal(engine.OPENAGENT_INTERN_DECISION_API_KEY, undefined);
    assert.equal(engine.OTEL_EXPORTER_OTLP_HEADERS, form.otlpHeaders);
    assert.equal(router.OTEL_EXPORTER_OTLP_TRACES_HEADERS, form.tracesHeaders);
    assert.equal(services.chat.environment, undefined);
    const nginx = config('nginx').services.nginx;
    assert.equal(nginx.volumes.find(v => v.target === '/etc/nginx/tls').source, join(dir, 'deploy/nginx/certs with spaces'));
    assert.deepEqual(nginx.ports.map(p => Number(p.published)), [8081, 8082, 8083]);
    const shell = spawnSync('bash', ['-c', 'source "$1"; printf "%s" "$OPENAGENT_TLS_CERT_DIR"', '_', join(dir, 'deploy/nginx/.env')], { encoding: 'utf8' });
    assert.equal(shell.status, 0, shell.stderr);
    assert.equal(shell.stdout, form.certDir);
    for (const password of ['trailing\\', "slash\\'quote", 'both"quotes\\', '$literal${UNSET}#hash', "quote'only"]) {
      const value = complete({ pgPassword: password, redisPassword: password, s3SecretKey: password });
      for (const file of buildEnvs(value).files) writeFileSync(join(dir, file.path), file.text);
      const env = config('openagent').services.engine.environment;
      assert.equal(env.ConnectionStrings__OpenAgentDatabase, postgresConnection(value), 'PostgreSQL escaping');
      assert.equal(env.ConnectionStrings__Redis, redisConnection(value), 'Redis escaping');
      assert.equal(env.FileAssets__ObjectStorage__SecretKey, password, 'S3 escaping');
    }
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('local server serves UI and blocks foreign origins, rebinding and non-JSON probes', async t => {
  const reservation = net.createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, ['server.js'], { cwd: fileURLToPath(new URL('../', import.meta.url)), env: { ...process.env, PORT: String(port) }, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => child.kill());
  await Promise.race([once(child.stdout, 'data'), once(child, 'exit').then(() => { throw new Error('Server exited before startup'); }), new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Server startup timeout')), 5000); timer.unref(); })]);
  const base = `http://127.0.0.1:${port}`;
  const ui = await fetch(base);
  assert.equal(ui.status, 200);
  assert.match(await ui.text(), /OpenAgent/);
  assert.equal(ui.headers.get('cache-control'), 'no-store');
  for (const headers of [{ Origin: 'https://foreign.test', 'Content-Type': 'application/json' }, { 'Content-Type': 'text/plain' }]) {
    const rebindingStatus = await new Promise(resolve => { http.get(base, { headers: { Host: 'rebind.test' } }, res => { res.resume(); resolve(res.statusCode); }); });
  assert.equal(rebindingStatus, 403);
  const response = await fetch(`${base}/api/probe/ports`, { method: 'POST', headers, body: '{}' });
    assert.ok([403, 415].includes(response.status));
  }
  const rebindingStatus = await new Promise(resolve => { http.get(base, { headers: { Host: 'rebind.test' } }, res => { res.resume(); resolve(res.statusCode); }); });
  assert.equal(rebindingStatus, 403);
  const response = await fetch(`${base}/api/probe/ports`, { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: base }, body: JSON.stringify({ ports: [port] }) });
  assert.equal(response.status, 200);
  assert.equal((await response.json())[0].free, false);
  const fixture = http.createServer((req, res) => {
    if (req.url === '/discovery') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ issuer: 'https://idp.test' })); }
    else { res.statusCode = 403; res.end(); }
  }).listen(0, '127.0.0.1');
  await once(fixture, 'listening');
  t.after(() => fixture.close());
  const endpoint = `http://127.0.0.1:${fixture.address().port}`;
  const post = async (kind, body) => (await fetch(`${base}/api/probe/${kind}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json();
  assert.equal((await post('http', { url: `${endpoint}/discovery` })).ok, true);
  assert.equal((await post('s3', { serviceUrl: endpoint, accessKey: 'fixture', secretKey: 'fixture' })).errorKind, 'auth');
});
