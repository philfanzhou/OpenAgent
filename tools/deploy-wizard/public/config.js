// Native environment names and file ownership follow deploy/*/.env.example.
export const FILES = [
  { id: 'shared', path: 'deploy/openagent/.env', label: '共享配置', description: 'Engine / Router 共用 · Redis、认证与遥测' },
  { id: 'engine', path: 'deploy/openagent/engine/.env', label: 'Engine', description: '数据库、文件存储与服务发现' },
  { id: 'router', path: 'deploy/openagent/router/.env', label: 'Router', description: 'Engine 路由与意图识别' },
  { id: 'nginx', path: 'deploy/nginx/.env', label: 'Nginx', description: '入口端口、证书与维护页目录' },
];
export const GROUP_DEFS = [
  { id: 'postgres', label: 'PostgreSQL', note: '分项填写，自动组合 Engine 数据库连接字符串。容器内填写服务地址。' },
  { id: 'redis', label: 'Redis', note: '共享给 Engine 和 Router；连接端口为服务内部端口。' },
  { id: 'auth', label: '身份认证', note: '默认沿用 dev 的 Production + JWT / Keycloak。请核对现有 IdP。' },
  { id: 'storage', label: '文件存储', note: '支持 MinIO 或兼容 S3 的对象存储，仅供 Engine 使用。' },
  { id: 'nginx', label: '访问入口', note: '仅调整宿主机发布端口；证书目录需包含 tls.crt 和 tls.key。' },
  { id: 'advanced', label: '运行与遥测', note: '按需覆盖应用默认值；镜像、项目名与网络名由 dev 固定。', advanced: true },
  { id: 'probe', label: '本机探测地址', note: '仅供本机检测，不写入配置。留空则使用上方服务地址；容器服务名可能无法在本机解析。', advanced: true },
];
const field = (group, key, label, options = {}) => ({ group, key, label, type: 'text', ...options });
export const FIELDS = [
  field('postgres', 'pgHost', '主机', { default: 'postgres', required: true }),
  field('postgres', 'pgPort', '端口', { type: 'port', default: 5432, required: true }),
  field('postgres', 'pgDatabase', '数据库', { default: 'openagent', required: true }),
  field('postgres', 'pgUser', '用户名', { default: 'openagent', required: true }),
  field('postgres', 'pgPassword', '密码', { type: 'password', gen: true, required: true }),
  field('postgres', 'pgSslMode', 'SSL 模式', { type: 'select', options: ['Prefer', 'Disable', 'Require', 'VerifyCA', 'VerifyFull'], default: 'Prefer' }),
  field('redis', 'redisHost', '主机', { default: 'redis', required: true }),
  field('redis', 'redisPort', '端口', { type: 'port', default: 6379, required: true }),
  field('redis', 'redisUser', 'ACL 用户名', { help: '可选；无 ACL 认证时留空。' }),
  field('redis', 'redisPassword', '密码', { type: 'password', help: '内建 Redis 无密码，留空即可。' }),
  field('redis', 'redisDatabase', '数据库编号', { type: 'number', min: 0, default: 0 }),
  field('redis', 'redisTls', '启用 TLS', { type: 'switch' }),
  field('auth', 'authMode', '认证模式', { env: 'Authentication__Mode', file: 'shared', type: 'select', options: ['JwtBearer', 'Basic'], default: 'JwtBearer', required: true }),
  field('auth', 'authority', '公开 Issuer / Realm URL', { env: 'Authentication__Authority', file: 'shared', default: 'https://agent.monolithicpower.com:8443/realms/openagent', cond: 'jwt', required: true, wide: true }),
  field('auth', 'metadataAddress', '内部 HTTPS Discovery URL', { env: 'Authentication__MetadataAddress', file: 'shared', default: 'https://openagent-infrastructure-keycloak-1:8443/realms/openagent/.well-known/openid-configuration', cond: 'jwt', wide: true }),
  field('auth', 'audience', 'Audience', { env: 'Authentication__Audience', file: 'shared', default: 'openagent-api', cond: 'jwt', required: true }),
  field('auth', 'clientId', 'Client ID', { env: 'Authentication__ClientId', file: 'shared', default: 'openagent-chat', cond: 'jwt', required: true }),
  field('auth', 'enableKeycloak', '启用 Keycloak 登录', { env: 'Authentication__EnableKeycloak', file: 'shared', type: 'switch', default: true, cond: 'jwt' }),
  field('auth', 'basicUsername', 'Basic 用户名', { env: 'Authentication__BasicUsername', file: 'shared', default: 'admin', cond: 'basic', required: true }),
  field('auth', 'basicTenantId', 'Basic 租户 ID', { env: 'Authentication__BasicTenantId', file: 'shared', default: 'development', cond: 'basic', required: true }),
  field('auth', 'basicPassword', 'Basic 密码', { env: 'Authentication__BasicPassword', file: 'shared', type: 'password', gen: true, cond: 'basic', required: true }),
  field('storage', 'fileAssetsEnabled', '启用文件资产', { env: 'FileAssets__Enabled', file: 'engine', type: 'switch', default: true }),
  field('storage', 's3ServiceUrl', 'S3 API 地址', { env: 'FileAssets__ObjectStorage__ServiceUrl', file: 'engine', default: 'http://minio:9000', cond: 'storage', required: true, wide: true }),
  field('storage', 's3AccessKey', 'Access Key', { env: 'FileAssets__ObjectStorage__AccessKey', file: 'engine', type: 'password', cond: 'storage', required: true }),
  field('storage', 's3SecretKey', 'Secret Key', { env: 'FileAssets__ObjectStorage__SecretKey', file: 'engine', type: 'password', cond: 'storage', required: true }),
  field('storage', 's3Bucket', 'Bucket', { env: 'FileAssets__ObjectStorage__BucketName', file: 'engine', default: 'openagent-files', omitDefault: true, cond: 'storage', required: true }),
  field('storage', 's3Region', 'Region', { env: 'FileAssets__ObjectStorage__Region', file: 'engine', default: 'us-east-1', omitDefault: true, cond: 'storage', required: true }),
  field('storage', 's3PathStyle', '使用 Path-style（MinIO）', { env: 'FileAssets__ObjectStorage__ForcePathStyle', file: 'engine', type: 'switch', default: true, cond: 'storage' }),
  field('storage', 'shareBaseUrl', '分享链接公开地址', { env: 'FileAssets__Share__PublicBaseUrl', file: 'engine', cond: 'storage', wide: true }),
  field('nginx', 'serverName', 'Server name', { env: 'OPENAGENT_NGINX_SERVER_NAME', file: 'nginx', default: 'localhost', required: true }),
  field('nginx', 'chatPort', 'Chat 端口', { env: 'OPENAGENT_CHAT_PORT', file: 'nginx', type: 'port', default: 8081, required: true }),
  field('nginx', 'routerPort', 'Router 端口', { env: 'OPENAGENT_ROUTER_PORT', file: 'nginx', type: 'port', default: 8082, required: true }),
  field('nginx', 'enginePort', 'Engine 端口', { env: 'OPENAGENT_ENGINE_PORT', file: 'nginx', type: 'port', default: 8083, required: true }),
  field('nginx', 'maxBodySize', '请求体上限', { env: 'OPENAGENT_NGINX_CLIENT_MAX_BODY_SIZE', file: 'nginx', default: '1100m', required: true }),
  field('nginx', 'certDir', 'TLS 证书目录', { env: 'OPENAGENT_TLS_CERT_DIR', file: 'nginx', default: './certs', required: true, help: '相对 deploy/nginx；已有证书也可填绝对路径。' }),
  field('nginx', 'maintenanceDir', '维护页目录', { env: 'OPENAGENT_MAINTENANCE_DIR', file: 'nginx', default: './maintenance', required: true, help: '目录内存在 maintenance.html 时，前端返回维护页。' }),
  field('advanced', 'environment', '运行环境', { env: 'ASPNETCORE_ENVIRONMENT', file: 'shared', type: 'select', options: ['Production', 'Development'], default: 'Production' }),
  field('advanced', 'enableApiKey', '启用 API Key 认证', { env: 'Authentication__EnableApiKey', file: 'shared', type: 'switch', omitDefault: true }),
  field('advanced', 'allowDevAnonymous', '允许开发匿名访问', { env: 'Authentication__AllowDevelopmentAnonymous', file: 'shared', type: 'switch', omitDefault: true }),
  field('advanced', 'allowedOrigin', 'CORS 来源（单个）', { env: 'Cors__AllowedOrigins__0', file: 'shared' }),
  field('advanced', 'applyMigrations', '启动时应用数据库迁移', { env: 'Database__ApplyMigrationsOnStartup', file: 'engine', type: 'switch', default: true }),
  field('advanced', 'heartbeatHost', '心跳内部主机', { env: 'Heartbeat__AdvertisedHost', file: 'engine', default: 'engine', required: true }),
  field('advanced', 'heartbeatPort', '心跳内部端口', { env: 'Heartbeat__AdvertisedPort', file: 'engine', type: 'port', default: 5208, required: true }),
  field('advanced', 'engineEndpoint', 'Router → Engine 地址', { env: 'RouterSettings__Routing__EngineEndpoint', file: 'router', default: 'http://engine:5208', required: true, wide: true }),
  field('advanced', 'intentTimeout', '意图识别超时（ms）', { env: 'RouterSettings__IntentRecognition__TimeoutMs', file: 'router', type: 'number', min: 1, default: 30000, required: true }),
  field('advanced', 'intentKey', 'Intern-Decision API Key', { env: 'OPENAGENT_INTERN_DECISION_API_KEY', file: 'router', type: 'password' }),
  field('advanced', 'otlpEndpoint', 'OTLP 通用出口', { env: 'OTEL_EXPORTER_OTLP_ENDPOINT', file: 'shared' }),
  field('advanced', 'otlpHeaders', 'OTLP Headers', { env: 'OTEL_EXPORTER_OTLP_HEADERS', file: 'shared', type: 'password' }),
  field('advanced', 'otlpProtocol', 'OTLP 协议', { env: 'OTEL_EXPORTER_OTLP_PROTOCOL', file: 'shared', type: 'select', options: ['', 'http/protobuf', 'grpc'] }),
  field('advanced', 'tracesEndpoint', 'Traces 专用出口', { env: 'OTEL_EXPORTER_OTLP_TRACES_ENDPOINT', file: 'shared' }),
  field('advanced', 'tracesHeaders', 'Traces Headers', { env: 'OTEL_EXPORTER_OTLP_TRACES_HEADERS', file: 'shared', type: 'password' }),
  field('probe', 'pgProbeHost', 'PostgreSQL 探测主机', { placeholder: 'localhost' }),
  field('probe', 'pgProbePort', 'PostgreSQL 探测端口', { type: 'port', placeholder: '5432' }),
  field('probe', 'redisProbeHost', 'Redis 探测主机', { placeholder: 'localhost' }),
  field('probe', 'redisProbePort', 'Redis 探测端口', { type: 'port', placeholder: '6379' }),
  field('probe', 's3ProbeUrl', 'S3 探测地址', { placeholder: 'http://localhost:9000', wide: true }),
  field('probe', 'keycloakProbeUrl', 'OIDC Discovery 探测地址', { wide: true }),
];

export const active = (f, form) => !f.cond || ({ jwt: form.authMode === 'JwtBearer', basic: form.authMode === 'Basic', storage: form.fileAssetsEnabled })[f.cond];
export function defaultForm() {
  return Object.fromEntries(FIELDS.map(f => [f.key, f.type === 'switch' ? (f.default ?? false) : String(f.default ?? '')]));
}

// Npgsql follows ADO.NET quoting; doubled double quotes preserve literal quotes.
const pgValue = value => /[;"'\s]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value;
export function postgresConnection(form) {
  return [['Host', form.pgHost], ['Port', form.pgPort], ['Database', form.pgDatabase], ['Username', form.pgUser], ['Password', form.pgPassword]]
    .map(([key, value]) => `${key}=${pgValue(String(value ?? ''))}`).concat(form.pgSslMode !== 'Prefer' ? [`SSL Mode=${form.pgSslMode}`] : []).join(';');
}
export function redisConnection(form) {
  const host = String(form.redisHost ?? '');
  const endpoint = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
  return [`${endpoint}:${form.redisPort}`, ...(form.redisUser ? [`user=${form.redisUser}`] : []),
    ...(form.redisPassword ? [`password=${form.redisPassword}`] : []),
    ...(String(form.redisDatabase) !== '0' ? [`defaultDatabase=${form.redisDatabase}`] : []),
    ...(form.redisTls ? ['ssl=true'] : [])].join(',');
}
// Compose single-quoted values are literal, including $ and #; escape single quotes.
export function quoteEnv(value) {
  const text = String(value);
  if (!text || /^[\w@%+=:,./~-]+$/.test(text)) return text;
  // A trailing backslash or backslash+quote cannot round-trip in single quotes.
  if (text.endsWith('\\') || text.includes("\\'")) return JSON.stringify(text).replaceAll('$', '$$$$');
  return `'${text.replaceAll("'", "\\'")}'`;
}

export function buildEnvs(values = {}) {
  const form = { ...defaultForm(), ...values };
  const errors = [];
  const invalidKeys = new Set();
  const error = (key, message) => { invalidKeys.add(key); errors.push({ key, message }); };
  for (const f of FIELDS.filter(f => active(f, form))) {
    const value = String(form[f.key] ?? '');
    if (/[\r\n\0]/.test(value)) error(f.key, `${f.label}不能含换行或空字符`);
    if (f.type === 'select' && !f.options.includes(value)) error(f.key, `${f.label}选项无效`);
    if (f.required && !value.trim()) error(f.key, `请填写${f.label}`);
    if (value && ['port', 'number'].includes(f.type) && (!/^\d+$/.test(value) || Number(value) < (f.min ?? 1) || !Number.isSafeInteger(Number(value)) || (f.type === 'port' && Number(value) > 65535))) error(f.key, `${f.label}数值无效`);
  }
  // StackExchange.Redis splits at commas and trims values; it provides no escaping.
  for (const key of ['redisUser', 'redisPassword']) {
    if (String(form[key]).includes(',') || String(form[key]) !== String(form[key]).trim()) error(key, 'Redis 用户名 / 密码不支持逗号或首尾空白');
  }
  for (const key of ['redisHost', 'pgHost']) {
    if (/[\s,=;\/]/.test(form[key]) || (String(form[key]).includes(':') && !/^\[?[0-9a-fA-F:]+\]?$/.test(form[key]))) error(key, '主机只填单个主机名或 IP，不含协议、端口或分隔符');
  }
  // deploy.sh sources the public Nginx file; keep its quoting valid in both Bash and Compose.
  for (const f of FIELDS.filter(f => f.file === 'nginx')) {
    if (/[\\']/.test(form[f.key])) error(f.key, `${f.label}不能含单引号或反斜杠`);
  }
  if (!/^[a-zA-Z0-9_.*:[\] -]+$/.test(form.serverName)) error('serverName', 'Server name 需为域名、IP 或空格分隔的名称');
  if (!/^\d+[kmg]?$/i.test(form.maxBodySize)) error('maxBodySize', '请求体上限需为数字及可选 k / m / g 单位');
  if (form.authMode === 'Basic' && form.basicUsername.includes(':')) error('basicUsername', 'Basic 用户名不能含冒号');
  if (form.allowDevAnonymous && form.environment !== 'Development') error('allowDevAnonymous', '匿名访问仅适用于 Development');
  for (const key of ['authority', 'metadataAddress', 's3ServiceUrl', 'shareBaseUrl', 'engineEndpoint', 'otlpEndpoint', 'tracesEndpoint', 's3ProbeUrl', 'keycloakProbeUrl', 'allowedOrigin']) {
    const f = FIELDS.find(f => f.key === key);
    if (!active(f, form) || !form[key]) continue;
    try {
      const url = new URL(form[key]);
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error();
      if (['authority', 'metadataAddress'].includes(key) && url.protocol !== 'https:') error(key, `${f.label}需使用 HTTPS`);
    } catch { error(key, `${f.label}需为完整 HTTP(S) 地址`); }
  }
  if (new Set(['chatPort', 'routerPort', 'enginePort'].map(key => form[key])).size !== 3) error('chatPort', 'Chat、Router、Engine 入口端口不能重复');
  const files = FILES.map(file => ({ ...file, lines: [`# ${file.path}`, '# 由 OpenAgent 部署向导生成；含凭据，请限制访问权限，切勿提交 Git。', ''] }));
  const append = (id, env, value) => files.find(f => f.id === id).lines.push(`${env}=${quoteEnv(value)}`);
  if (!['pgHost', 'pgPort', 'pgDatabase', 'pgUser', 'pgPassword', 'pgSslMode'].some(k => invalidKeys.has(k))) append('engine', 'ConnectionStrings__OpenAgentDatabase', postgresConnection(form));
  if (!['redisHost', 'redisPort', 'redisUser', 'redisPassword', 'redisDatabase'].some(k => invalidKeys.has(k))) append('shared', 'ConnectionStrings__Redis', redisConnection(form));
  append('shared', 'Authentication__EnableKeycloak', form.authMode === 'JwtBearer' && form.enableKeycloak);
  for (const f of FIELDS) {
    if (!f.env || f.key === 'enableKeycloak' || !active(f, form) || invalidKeys.has(f.key)) continue;
    const value = form[f.key];
    if (value === '' && !f.required) continue;
    if (f.omitDefault && String(value) === String(f.default ?? false)) continue;
    append(f.file, f.env, value);
  }
  return { files: files.map(({ lines, ...file }) => ({ ...file, text: lines.join('\n') + '\n' })), errors };
}
export function collectProbePorts(form) {
  return [...new Set(['chatPort', 'routerPort', 'enginePort'].map(key => Number(form[key])).filter(n => Number.isInteger(n) && n > 0 && n < 65536))];
}
export function keycloakWellKnownUrl(form) {
  return form.keycloakProbeUrl || `${String(form.authority).replace(/\/+$/, '')}/.well-known/openid-configuration`;
}
export function randomPassword(length = 24) {
  const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789_-';
  return Array.from(crypto.getRandomValues(new Uint8Array(length)), b => chars[b % chars.length]).join('');
}
