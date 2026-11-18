// deploy-wizard 服务端：托管 public/ 静态页面 + 5 个基础设施探测端点。
// 安全约定：默认只绑 127.0.0.1；不打印请求体（凭据不进日志）；不做任何持久化。
import express from 'express';
import net from 'node:net';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import Redis from 'ioredis';
import { S3Client, HeadBucketCommand, ListBucketsCommand } from '@aws-sdk/client-s3';

const HOST = '127.0.0.1';
const PORT = Number(process.env.PORT) || 5178;
const TIMEOUT_MS = 5000;

const app = express();
// Reject cross-site requests and DNS rebinding before accepting credentials or probing.
app.use((req, res, next) => {
  const hosts = [`127.0.0.1:${PORT}`, `localhost:${PORT}`];
  if (!hosts.includes(req.headers.host)) return res.sendStatus(403);
  if (req.headers.origin && req.headers.origin !== `http://${req.headers.host}`) return res.sendStatus(403);
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'");
  if (req.method === 'POST' && !req.is('application/json')) return res.sendStatus(415);
  next();
});
app.use(express.json({ limit: '64kb' }));
app.use(express.static(fileURLToPath(new URL('./public', import.meta.url))));

// ── 工具 ────────────────────────────────────────────────────────────────

const HINTS = {
  dns: '域名解析失败：检查主机名拼写',
  refused: '连接被拒绝：服务未启动，或端口不对（宿主机端口是映射左侧的数字）',
  timeout: '连接超时：检查防火墙/安全组，或目标不在线',
  auth: '认证失败：用户名或密码/密钥不正确',
  db_not_found: '数据库不存在：确认数据库名',
  bucket_not_found: '桶不存在：内建 MinIO 由 minio-init 自动建桶，需先启动 compose',
  cert: 'TLS 证书异常：自签证书需本机信任，或改用 http 探测',
  in_use: '端口已被占用：换一个宿主机端口（env 中映射左侧数字）',
  invalid: '端口无效：需为 1-65535',
  http_status: 'HTTP 状态异常：检查路径与服务状态',
  unknown: '探测失败，详见详情',
};

function ok(detail, extra = {}) {
  return { ok: true, errorKind: null, hint: null, detail, ...extra };
}

function fail(errorKind, detail, extra = {}) {
  return { ok: false, errorKind, hint: HINTS[errorKind] || HINTS.unknown, detail, ...extra };
}

// 网络层错误 → 统一分类（pg/ioredis/fetch 的 cause 都可能带这些 code）
function classifyNetworkError(err) {
  const code = err?.code || err?.cause?.code || '';
  const msg = String(err?.message || '');
  if (code === 'ENOTFOUND' || code === 'EAI_AGAIN' || /ENOTFOUND/.test(msg)) return 'dns';
  if (code === 'ECONNREFUSED' || /ECONNREFUSED/.test(msg)) return 'refused';
  if (code === 'ETIMEDOUT' || /timeout|timed? ?out|abort/i.test(msg) || err?.name === 'TimeoutError') return 'timeout';
  return null;
}

function firstLine(text, max = 200) {
  return String(text || '').replace(/\s+/g, ' ').trim().slice(0, max);
}

// Windows 上 ECONNREFUSED 常表现为 AggregateError（多个地址尝试），递归找首个可读消息
function errDetail(err) {
  const candidates = [];
  const visit = (e, depth = 0) => {
    if (!e || depth > 3) return;
    if (e.message && !/^AggregateError$/.test(e.message)) candidates.push(e.message);
    if (Array.isArray(e.errors)) e.errors.forEach((x) => visit(x, depth + 1));
    visit(e.cause, depth + 1);
  };
  visit(err);
  return firstLine(candidates[0] || err?.message || String(err));
}

/** 执行探测并统一包装 ok/fail 结果；kindFn 决定协议特定的错误分类 */
async function withLatency(fn, kindFn = classifyNetworkError) {
  const started = Date.now();
  try {
    const detail = await fn();
    return { ...ok(detail), latencyMs: Date.now() - started };
  } catch (err) {
    const kind = kindFn(err) || 'unknown';
    return { ...fail(kind, errDetail(err)), latencyMs: Date.now() - started };
  }
}

// ── 探测实现 ────────────────────────────────────────────────────────────

async function probePostgres({ host = 'localhost', port = 5432, user = 'openagent', password = '', database = 'openagent', sslMode = 'Prefer', servername } = {}) {
  const ssl = sslMode === 'Disable' ? false : {
    rejectUnauthorized: ['VerifyCA', 'VerifyFull'].includes(sslMode),
    ...(sslMode === 'VerifyCA' ? { checkServerIdentity: () => undefined } : {}),
    ...(servername && !net.isIP(servername) ? { servername } : {}),
  };
  const query = async (sslOptions) => {
    const client = new pg.Client({
      host, port: Number(port) || 5432, user, password, database,
      connectionTimeoutMillis: TIMEOUT_MS, query_timeout: TIMEOUT_MS, ssl: sslOptions,
    });
    try {
      await client.connect();
      const { rows } = await client.query('select version() as v');
      return firstLine(rows[0]?.v, 120);
    } finally {
      await client.end().catch(() => {});
    }
  };
  return withLatency(async () => {
    try { return await query(ssl); }
    catch (err) {
      if (sslMode === 'Prefer' && /does not support SSL/i.test(err.message)) return query(false);
      throw err;
    }
  }, postgresKind);
}

function postgresKind(err) {
  if (err?.code === '28P01') return 'auth';
  if (err?.code === '3D000') return 'db_not_found';
  return classifyNetworkError(err);
}

// 等待 ioredis 进入 ready；首错（refused/超时/认证）直接拒绝
function waitForRedisReady(client, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      cleanup();
      const err = new Error('连接超时');
      err.code = 'ETIMEDOUT';
      reject(err);
    }, timeoutMs);
    const onReady = () => { cleanup(); resolve(); };
    const onError = (err) => { cleanup(); reject(err); };
    const cleanup = () => {
      clearTimeout(timer);
      client.off('ready', onReady);
      client.off('error', onError);
    };
    client.once('ready', onReady);
    client.once('error', onError);
  });
}

async function probeRedis({ host = 'localhost', port = 6379, password = '', user = '', database = 0, tls = false, servername } = {}) {
  const client = new Redis({
    host: host.replace(/^\[|\]$/g, ''), port: Number(port) || 6379, password: password || undefined,
    username: user || undefined, db: Number(database),
    tls: tls ? { ...(servername && !net.isIP(servername) ? { servername } : {}) } : undefined,
    connectTimeout: TIMEOUT_MS, commandTimeout: TIMEOUT_MS, maxRetriesPerRequest: 1, retryStrategy: () => null,
    enableOfflineQueue: false,
  });
  // ioredis 的底层网络错误经 'error' 事件抛出，命令层可能只拿到 "Connection is closed."
  let lastError = null;
  client.on('error', (err) => { lastError = err; });
  const started = Date.now();
  try {
    await waitForRedisReady(client, TIMEOUT_MS);
    await client.ping();
    const info = await client.info('server');
    const version = (info.match(/redis_version:(.+)/) || [])[1]?.trim();
    return { ...ok(version ? `PONG（Redis ${version}）` : 'PONG'), latencyMs: Date.now() - started };
  } catch (err) {
    const real = lastError && /connection is closed/i.test(String(err?.message)) ? lastError : err;
    return { ...fail(redisKind(real) || 'unknown', errDetail(real)), latencyMs: Date.now() - started };
  } finally {
    client.disconnect();
  }
}

function redisKind(err) {
  const msg = String(err?.message || '');
  if (/NOAUTH|WRONGPASS|invalid password|AUTH/i.test(msg)) return 'auth';
  return classifyNetworkError(err);
}

function normalizeEndpoint(url) {
  const trimmed = String(url || '').trim().replace(/\/+$/, '');
  if (!trimmed) throw new Error('S3 地址为空');
  return /^https?:\/\//.test(trimmed) ? trimmed : `http://${trimmed}`;
}

async function probeS3({ serviceUrl = '', accessKey = '', secretKey = '', bucket = 'openagent-files', region = 'us-east-1', forcePathStyle = true } = {}) {
  const client = new S3Client({
    endpoint: normalizeEndpoint(serviceUrl),
    region, forcePathStyle,
    maxAttempts: 1,
    credentials: { accessKeyId: accessKey, secretAccessKey: secretKey },
    requestHandler: { requestTimeout: TIMEOUT_MS },
  });
  return withLatency(async () => {
    try {
      await client.send(new HeadBucketCommand({ Bucket: bucket }));
      return `桶 ${bucket} 可访问`;
    } catch (headErr) {
      if (s3Kind(headErr) !== 'bucket_not_found') throw headErr;
      // 桶不存在：降级 ListBuckets 区分"凭据有效但桶没建"与"凭据错误"
      try {
        const listed = await client.send(new ListBucketsCommand({}));
        const names = (listed.Buckets || []).map((b) => b.Name);
        if (names.includes(bucket)) return `桶 ${bucket} 可访问`;
        const err = new Error(`凭据有效；桶 ${bucket} 不存在（现有：${names.slice(0, 5).join('、') || '无'}）`);
        err.s3Kind = 'bucket_not_found';
        throw err;
      } catch (listErr) {
        listErr.s3Kind = s3Kind(listErr) === 'bucket_not_found' ? 'auth' : listErr.s3Kind;
        throw listErr;
      }
    } finally {
      client.destroy();
    }
  }, s3Kind);
}

function s3Kind(err) {
  if (err?.s3Kind) return err.s3Kind;
  const status = err?.$metadata?.httpStatusCode;
  const name = String(err?.name || '');
  if (status === 401 || status === 403 || name === 'InvalidAccessKeyId' || name === 'SignatureDoesNotMatch') return 'auth';
  if (status === 404 || name === 'NotFound' || name === 'NoSuchBucket') return 'bucket_not_found';
  return classifyNetworkError(err);
}

async function probeHttp({ url = '' } = {}) {
  const target = String(url || '').trim();
  if (!/^https?:\/\//.test(target)) {
    return { ...fail('invalid', 'URL 需以 http(s):// 开头'), hint: 'URL 需以 http(s):// 开头' };
  }
  return withLatency(async () => {
    let res;
    try {
      res = await fetch(target, { redirect: 'follow', signal: AbortSignal.timeout(TIMEOUT_MS) });
    } catch (err) {
      const markers = `${err.code || ''}${err.cause?.code || ''}${err.cause?.message || ''}`;
      if (/CERT|SELF_SIGNED|DEPTH_ZERO/i.test(markers)) {
        const err2 = new Error(firstLine(err.cause?.message || err.message));
        err2.httpKind = 'cert';
        throw err2;
      }
      throw err;
    }
    if (!res.ok) {
      const err = new Error(`HTTP ${res.status} ${res.statusText}`);
      err.httpKind = 'http_status';
      throw err;
    }
    let detail = `HTTP ${res.status}`;
    try {
      const body = await res.json();
      if (body?.issuer) detail += `；issuer=${body.issuer}`;
    } catch { /* 非 JSON 响应也算连通 */ }
    return detail;
  }, httpKind);
}

function httpKind(err) {
  return err?.httpKind || classifyNetworkError(err);
}

// 端口占用检测：尝试在宿主机上绑定，成功 = 空闲
async function probePorts({ ports = [] } = {}) {
  const list = [...new Set(ports.map(Number).filter((n) => Number.isInteger(n) && n >= 1 && n <= 65535))];
  const results = await Promise.all(list.map(async port => {
    const wildcard = await tryBind(port, '0.0.0.0');
    // macOS may allow wildcard + loopback binds with SO_REUSEADDR. Check both.
    return wildcard.free ? tryBind(port, '127.0.0.1') : wildcard;
  }));
  return results;
}

function tryBind(port, host) {
  return new Promise((resolve) => {
    const server = net.createServer();
    const done = (result) => {
      server.removeAllListeners('error');
      server.close(() => resolve(result));
    };
    server.once('error', (err) => {
      if (err.code === 'EADDRINUSE') resolve({ port, free: false, errorKind: 'in_use', hint: HINTS.in_use });
      else if (err.code === 'EACCES') resolve({ port, free: false, errorKind: 'invalid', hint: '无权限绑定该端口' });
      else resolve({ port, free: false, errorKind: 'unknown', hint: HINTS.unknown });
    });
    server.once('listening', () => done({ port, free: true, errorKind: null, hint: null }));
    server.listen(port, host);
  });
}

// ── 路由 ────────────────────────────────────────────────────────────────

const asyncRoute = (fn) => (req, res) => {
  fn(req, res).catch((err) => {
    // Do not log errors containing user-supplied URLs or credentials.
    res.status(500).json({ ok: false, errorKind: 'unknown', hint: HINTS.unknown, detail: '服务端执行异常' });
  });
};

app.post('/api/probe/postgres', asyncRoute(async (req, res) => res.json(await probePostgres(req.body ?? {}))));
app.post('/api/probe/redis', asyncRoute(async (req, res) => res.json(await probeRedis(req.body ?? {}))));
app.post('/api/probe/s3', asyncRoute(async (req, res) => res.json(await probeS3(req.body ?? {}))));
app.post('/api/probe/http', asyncRoute(async (req, res) => res.json(await probeHttp(req.body ?? {}))));
app.post('/api/probe/ports', asyncRoute(async (req, res) => res.json(await probePorts(req.body ?? {}))));

app.use('/api', (req, res) => res.status(404).json({ error: 'not found' }));
app.use((err, req, res, next) => res.status(400).json(fail('invalid', '请求格式无效')));

app.listen(PORT, HOST, () => {
  console.log(`deploy-wizard 已启动: http://${HOST}:${PORT}（仅本机可访问）`);
});
