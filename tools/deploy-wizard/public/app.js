// deploy-wizard 前端：表单渲染、.env 实时预览、连通性探测面板。
// 原生 ESM，零依赖；用户数据一律经 textContent/value 写入，不拼 HTML。
import {
  FILES, GROUP_DEFS, FIELDS, active, defaultForm, buildEnvs, postgresConnection, redisConnection,
  collectProbePorts, keycloakWellKnownUrl, randomPassword,
} from './config.js';

const LS_FORM = 'dw.v2.form';
const LS_REMEMBER = 'dw.v2.remember';
const SECRET_KEYS = new Set(FIELDS.filter((f) => f.type === 'password').map((f) => f.key));

const state = defaultForm();

// ── 状态恢复（仅当勾选"记住表单"，且不含密码） ──
const rememberEl = document.getElementById('remember');
try {
  if (localStorage.getItem(LS_REMEMBER) === '1') {
    rememberEl.checked = true;
    const saved = JSON.parse(localStorage.getItem(LS_FORM) || '{}');
    for (const f of FIELDS) {
      if (f.type === 'password') continue;
      if (saved[f.key] !== undefined) state[f.key] = saved[f.key];
    }
  }
} catch { /* localStorage 不可用则忽略 */ }

function persist() {
  if (!rememberEl.checked) return;
  const data = {};
  for (const f of FIELDS) {
    if (SECRET_KEYS.has(f.key)) continue;
    data[f.key] = state[f.key];
  }
  try { localStorage.setItem(LS_FORM, JSON.stringify(data)); } catch { /* ignore */ }
}

rememberEl.addEventListener('change', () => {
  try {
    if (rememberEl.checked) {
      localStorage.setItem(LS_REMEMBER, '1');
      persist();
    } else {
      localStorage.removeItem(LS_REMEMBER);
      localStorage.removeItem(LS_FORM);
    }
  } catch { /* ignore */ }
});

// ── 表单渲染 ──
const formEl = document.getElementById('form');
const inputs = new Map(); // key -> input 元素

function setValue(key, value, { silent = false } = {}) {
  state[key] = value;
  const input = inputs.get(key);
  if (input) { if (input.type === 'checkbox') input.checked = Boolean(value); else input.value = value; }
  if (!silent) onFormChange();
}

function makeField(f) {
  const wrap = document.createElement('div');
  wrap.className = (f.type === 'switch' ? 'switch-field' : 'field') + (f.wide ? ' wide' : '');
  if (f.cond) wrap.dataset.cond = f.cond;

  if (f.type === 'switch') {
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.id = `f-${f.key}`;
    input.checked = state[f.key] === true || state[f.key] === 'true';
    input.addEventListener('change', () => {
      state[f.key] = input.checked;
      onFormChange();
    });
    inputs.set(f.key, input);
    wrap.append(input);
    const label = document.createElement('label');
    label.htmlFor = input.id;
    label.textContent = f.label;
    wrap.append(label);
    if (f.help) {
      const help = document.createElement('div');
      help.className = 'help';
      help.style.gridColumn = '2 / -1';
      help.textContent = f.help;
      wrap.append(help);
    }
    return wrap;
  }

  const label = document.createElement('label');
  label.textContent = f.label + (f.required ? ' *' : '');

  let input;
  if (f.type === 'select') {
    input = document.createElement('select');
    for (const opt of f.options) {
      const option = document.createElement('option');
      option.value = opt;
      option.textContent = opt;
      input.append(option);
    }
    input.value = state[f.key];
  } else {
    input = document.createElement('input');
    input.type = ['port', 'number'].includes(f.type) ? 'number' : f.type === 'password' ? 'password' : 'text';
    if (['port', 'number'].includes(f.type)) { input.min = f.min ?? 1; if (f.type === 'port') input.max = 65535; input.step = 1; input.placeholder = f.placeholder ?? ''; }
    else if (f.placeholder) input.placeholder = f.placeholder;
    input.value = state[f.key] ?? '';
  }
  input.id = `f-${f.key}`;
  label.htmlFor = input.id;
  wrap.append(label);
  input.addEventListener('input', () => {
    state[f.key] = input.value;
    onFormChange();
  });
  input.autocomplete = f.type === 'password' ? 'new-password' : 'off';
  input.spellcheck = false;
  inputs.set(f.key, input);
  wrap.append(input);

  if (f.gen) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'gen';
    btn.textContent = '生成';
    btn.title = '生成强密码';
    btn.addEventListener('click', () => setValue(f.key, randomPassword()));
    wrap.append(btn);
  }

  if (f.help) {
    const help = document.createElement('div');
    help.className = 'help';
    help.textContent = f.help;
    wrap.append(help);
  }
  return wrap;
}

const connections = new Map();
for (const [index, g] of GROUP_DEFS.entries()) {
  const section = document.createElement(g.advanced ? 'details' : 'fieldset');
  section.id = `section-${g.id}`;
  if (g.advanced) section.className = 'form-section';
  const heading = document.createElement(g.advanced ? 'summary' : 'legend');
  heading.textContent = g.label;
  const note = document.createElement('p');
  note.className = 'group-note';
  note.textContent = g.note;
  const container = document.createElement('div');
  container.className = 'fields';
  for (const f of FIELDS.filter(f => f.group === g.id)) container.append(makeField(f));
  section.append(heading, note, container);
  if (['postgres', 'redis'].includes(g.id)) {
    const connection = document.createElement('div');
    connection.className = 'connection';
    connections.set(g.id, connection);
    section.append(connection);
  }
  formEl.append(section);
  const link = document.createElement('a');
  link.href = `#${section.id}`;
  link.textContent = `${String(index + 1).padStart(2, '0')}   ${g.label}`;
  link.addEventListener('click', () => {
    if (g.advanced) section.open = true;
    document.querySelectorAll('#sections a').forEach(a => a.classList.toggle('selected', a === link));
  });
  document.getElementById('sections').append(link);
}
formEl.addEventListener('submit', event => event.preventDefault());

function refreshCond() {
  for (const f of FIELDS) {
    const input = inputs.get(f.key);
    const enabled = active(f, state);
    input.closest('.field, .switch-field').classList.toggle('hidden', !enabled);
    input.disabled = !enabled;
  }
}

const previewEl = document.getElementById('envPreview');
const bannerEl = document.getElementById('missingBanner');
const showSecrets = document.getElementById('showSecrets');
let currentFile = 'shared';
let output;
for (const file of FILES) {
  const tab = document.createElement('button');
  tab.type = 'button';
  tab.role = 'tab';
  tab.dataset.file = file.id;
  tab.textContent = file.label;
  tab.addEventListener('click', () => { currentFile = file.id; updatePreview(); });
  tab.addEventListener('keydown', event => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const index = FILES.findIndex(f => f.id === currentFile);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? FILES.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + FILES.length) % FILES.length;
    document.querySelector(`[data-file="${FILES[next].id}"]`).click();
    document.querySelector(`[data-file="${FILES[next].id}"]`).focus();
  });
  document.getElementById('fileTabs').append(tab);
}
showSecrets.addEventListener('change', updatePreview);
function updatePreview() {
  output = buildEnvs(state);
  const file = output.files.find(f => f.id === currentFile);
  const masked = { ...state };
  for (const key of SECRET_KEYS) if (masked[key]) masked[key] = '********';
  const displayForm = showSecrets.checked ? state : masked;
  previewEl.textContent = showSecrets.checked ? file.text : buildEnvs(masked).files.find(f => f.id === currentFile).text;
  document.getElementById('filePath').textContent = file.path;
  document.getElementById('fileDescription').textContent = file.description;
  document.getElementById('lineCount').textContent = `${file.text.split('\n').length - 1} 行`;
  for (const tab of document.querySelectorAll('[data-file]')) {
    tab.setAttribute('aria-selected', tab.dataset.file === currentFile);
    tab.tabIndex = tab.dataset.file === currentFile ? 0 : -1;
  }
  for (const [kind, el] of connections) {
    const label = document.createElement('span');
    label.textContent = kind === 'postgres' ? 'ConnectionStrings__OpenAgentDatabase · 自动组合' : 'ConnectionStrings__Redis · 自动组合';
    el.replaceChildren(label, document.createTextNode(kind === 'postgres' ? postgresConnection(displayForm) : redisConnection(displayForm)));
  }
  for (const [key, input] of inputs) input.setAttribute('aria-invalid', output.errors.some(e => e.key === key));
  const badge = document.getElementById('readyBadge');
  badge.textContent = output.errors.length ? `${output.errors.length} 项待完善` : '可以导出';
  badge.classList.toggle('ready', !output.errors.length);
  bannerEl.replaceChildren();
  for (const error of output.errors) {
    const link = document.createElement('a');
    link.href = `#f-${error.key}`;
    link.textContent = error.message;
    link.addEventListener('click', event => {
      event.preventDefault();
      const input = inputs.get(error.key);
      const details = input.closest('details');
      if (details) details.open = true;
      input.focus();
    });
    bannerEl.append(link, document.createTextNode(' · '));
  }
  bannerEl.classList.toggle('hidden', !output.errors.length);
  document.getElementById('copyBtn').disabled = output.errors.length > 0;
  document.getElementById('downloadBtn').disabled = output.errors.length > 0;
}

function onFormChange() {
  refreshCond();
  updatePreview();
  persist();
  refreshTargets();
}

// ── 复制 / 下载 ──
const copyBtn = document.getElementById('copyBtn');
copyBtn.addEventListener('click', async () => {
  if (output.errors.length) return;
  const text = output.files.find(f => f.id === currentFile).text;
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = Object.assign(document.createElement('textarea'), { value: text });
    document.body.append(ta);
    ta.select();
    document.execCommand('copy');
    ta.remove();
  }
  copyBtn.textContent = '已复制 ✓';
  setTimeout(() => { copyBtn.textContent = '复制当前文件'; }, 1200);
});

document.getElementById('downloadBtn').addEventListener('click', () => {
  if (output.errors.length) return;
  const blob = new Blob([output.files.find(f => f.id === currentFile).text], { type: 'text/plain;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `${currentFile}.env`;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
});

// ── 探测面板 ──
const probeCardsEl = document.getElementById('probeCards');
const targets = []; // {name, el, summarize(), buildBody(), path}

function makeProbeCard({ name, path, buildBody, summarize }) {
  const card = document.createElement('div');
  card.className = 'probe';
  const head = document.createElement('div');
  head.className = 'probe-head';
  const nameEl = document.createElement('span');
  nameEl.className = 'name';
  nameEl.textContent = name;
  const targetEl = document.createElement('span');
  targetEl.className = 'target';
  head.append(nameEl, targetEl);
  const result = document.createElement('div');
  result.className = 'probe-result';
  result.textContent = '未测试';
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.textContent = '测试';
  btn.style.marginTop = '8px';
  btn.addEventListener('click', () => runProbe(entry, btn));
  card.append(head, result, btn);
  probeCardsEl.append(card);
  const entry = { name, path, buildBody, summarize, targetEl, result, btn, card };
  targets.push(entry);
  return entry;
}

function summarizePg() {
  return `${state.pgProbeHost || state.pgHost}:${state.pgProbePort || state.pgPort}/${state.pgDatabase || 'openagent'}`;
}
function summarizeRedis() {
  return `${state.redisProbeHost || state.redisHost}:${state.redisProbePort || state.redisPort}`;
}
function summarizeS3() {
  const url = (state.s3ProbeUrl || state.s3ServiceUrl || '').trim();
  return `${url || '(未填 S3 地址)'} · ${state.s3Bucket || 'openagent-files'}`;
}

makeProbeCard({
  name: 'PostgreSQL',
  path: '/api/probe/postgres',
  summarize: summarizePg,
  buildBody: () => ({
    host: state.pgProbeHost || state.pgHost,
    port: state.pgProbePort || state.pgPort,
    user: state.pgUser || 'openagent',
    password: state.pgPassword || '',
    database: state.pgDatabase || 'openagent',
    sslMode: state.pgSslMode,
    servername: state.pgHost,
  }),
});

makeProbeCard({
  name: 'Redis',
  path: '/api/probe/redis',
  summarize: summarizeRedis,
  buildBody: () => ({
    host: state.redisProbeHost || state.redisHost,
    port: state.redisProbePort || state.redisPort,
    password: state.redisPassword || '',
    user: state.redisUser || '',
    database: state.redisDatabase,
    tls: state.redisTls,
    servername: state.redisHost,
  }),
});

makeProbeCard({
  name: 'MinIO / S3',
  path: '/api/probe/s3',
  summarize: summarizeS3,
  buildBody: () => ({
    serviceUrl: (state.s3ProbeUrl || state.s3ServiceUrl || '').trim(),
    accessKey: state.s3AccessKey || '',
    secretKey: state.s3SecretKey || '',
    bucket: state.s3Bucket || 'openagent-files',
    region: state.s3Region,
    forcePathStyle: state.s3PathStyle,
  }),
});

makeProbeCard({
  name: 'Keycloak (OIDC)',
  path: '/api/probe/http',
  summarize: () => keycloakWellKnownUrl(state),
  buildBody: () => ({ url: keycloakWellKnownUrl(state) }),
});

// 端口占用卡片
const portsCard = document.createElement('div');
portsCard.className = 'probe';
portsCard.innerHTML = `
  <div class="probe-head"><span class="name">宿主机端口占用</span></div>
  <div class="port-chips"><span class="chip empty">在左侧填写端口后测试</span></div>
  <button type="button" style="margin-top:8px">测试</button>`;
probeCardsEl.append(portsCard);
const portChips = portsCard.querySelector('.port-chips');
const portsBtn = portsCard.querySelector('button');

function renderPortChips(results) {
  portChips.textContent = '';
  const list = collectProbePorts(state);
  if (list.length === 0) {
    const chip = document.createElement('span');
    chip.className = 'chip empty';
    chip.textContent = '在左侧填写端口后测试';
    portChips.append(chip);
    return;
  }
  const byPort = new Map(results.map((r) => [r.port, r]));
  for (const port of list) {
    const chip = document.createElement('span');
    const r = byPort.get(port);
    chip.className = r ? (r.free ? 'chip free' : 'chip used') : 'chip empty';
    chip.textContent = r ? `${port} ${r.free ? '空闲' : '占用'}` : `${port} …`;
    portChips.append(chip);
  }
}

portsBtn.addEventListener('click', async () => {
  const ports = collectProbePorts(state);
  if (ports.length === 0) return;
  portsBtn.disabled = true;
  try {
    const res = await fetch('/api/probe/ports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ports }),
    });
    renderPortChips(await res.json());
  } catch (err) {
    renderPortChips([]);
  } finally {
    portsBtn.disabled = false;
  }
});

function refreshTargets() {
  for (const t of targets) {
    t.targetEl.textContent = t.summarize();
    t.card.classList.toggle('hidden', (t.name === 'Keycloak (OIDC)' && state.authMode !== 'JwtBearer') || (t.name === 'MinIO / S3' && !state.fileAssetsEnabled));
    t.result.textContent = '未测试';
  }
  renderPortChips([]);
}

async function runProbe(entry, btn) {
  if (btn.disabled) return;
  const requestBody = JSON.stringify(entry.buildBody());
  btn.disabled = true;
  entry.result.textContent = '测试中…';
  try {
    const res = await fetch(entry.path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: requestBody,
    });
    const data = await res.json();
    if (requestBody === JSON.stringify(entry.buildBody())) renderResult(entry, data);
  } catch (err) {
    renderResult(entry, { ok: false, hint: '无法连接本机 deploy-wizard 服务', detail: String(err?.message || err) });
  } finally {
    btn.disabled = false;
  }
}

function renderResult(entry, data) {
  const { result } = entry;
  result.textContent = '';
  const badge = document.createElement('span');
  badge.className = `badge ${data.ok ? 'ok' : 'fail'}`;
  badge.textContent = data.ok ? `通过 ${data.latencyMs ?? '—'}ms` : '失败';
  result.append(badge);
  if (data.detail) {
    const detail = document.createElement('div');
    detail.className = 'detail';
    detail.textContent = data.detail;
    result.append(detail);
  }
  if (!data.ok && data.hint) {
    const hint = document.createElement('div');
    hint.className = 'hint-line';
    hint.textContent = `建议：${data.hint}`;
    result.append(hint);
  }
}

document.getElementById('probeAllBtn').addEventListener('click', () => {
  for (const t of targets) if (!t.card.classList.contains('hidden')) runProbe(t, t.btn);
  portsBtn.click();
});

// ── 初始化 ──
onFormChange();
