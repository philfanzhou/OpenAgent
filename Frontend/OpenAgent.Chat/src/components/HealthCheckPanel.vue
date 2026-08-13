<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, fetchHealth, fetchHealthReport, getConnectionMode, getEngineBaseUrl, getRouterBaseUrl } from '../api'
import { loadHealthCheckCache, mergeChecksWithSeed, saveHealthCheckCache, type CheckGroup, type CheckItem, type CheckStatus } from '../healthCheckCache'
import type { HealthReport } from '../types'

const mode = ref(getConnectionMode())
const engineUrl = ref(getEngineBaseUrl())
const routerUrl = ref(getRouterBaseUrl())
const running = ref(false)
const checks = ref<CheckItem[]>([])
const expanded = ref<Record<string, boolean>>({})
const ranAt = ref('')

const groupMeta: Record<CheckGroup, { label: string; eyebrow: string }> = {
  services: { label: '服务连接', eyebrow: 'SERVICES' },
  infrastructure: { label: '基础设施', eyebrow: 'INFRASTRUCTURE' },
  data: { label: '数据与能力', eyebrow: 'DATA & CAPABILITIES' },
}
const groupOrder: CheckGroup[] = ['services', 'infrastructure', 'data']

const overall = computed(() => {
  const done = checks.value.filter(item => item.status === 'ok' || item.status === 'warn' || item.status === 'error')
  if (!done.length) return { status: 'idle' as CheckStatus, label: '待检测' }
  if (done.some(item => item.status === 'error')) return { status: 'error', label: '存在异常' }
  if (done.some(item => item.status === 'warn')) return { status: 'warn', label: '部分降级' }
  return { status: 'ok', label: '平台健康' }
})

const summary = computed(() => {
  const count = (status: CheckStatus) => checks.value.filter(item => item.status === status).length
  return `${count('ok')} 项正常 · ${count('warn')} 项降级 · ${count('error')} 项异常`
})

function formatRunAt(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

const runAtLabel = computed(() => {
  if (!ranAt.value) return ''
  const elapsedMs = Date.now() - new Date(ranAt.value).getTime()
  if (Number.isNaN(elapsedMs) || elapsedMs < 0) return formatRunAt(ranAt.value)
  const minutes = Math.floor(elapsedMs / 60000)
  if (minutes < 1) return `刚刚（${formatRunAt(ranAt.value)}）`
  if (minutes < 60) return `${minutes} 分钟前（${formatRunAt(ranAt.value)}）`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时前（${formatRunAt(ranAt.value)}）`
  return `${Math.floor(hours / 24)} 天前（${formatRunAt(ranAt.value)}）`
})

function seeded(): CheckItem[] {
  const services: CheckItem[] = [
    { key: 'engine', group: 'services', name: 'Engine 服务', detail: '待检测', status: 'idle' },
    ...(mode.value === 'router'
      ? [{ key: 'router', group: 'services' as CheckGroup, name: 'Router 服务', detail: '待检测', status: 'idle' as CheckStatus }]
      : []),
    { key: 'identity', group: 'services', name: '认证身份', detail: '待检测', status: 'idle' },
  ]
  const infra: CheckItem[] = [
    { key: 'redis', group: 'infrastructure', name: 'Redis', detail: '待检测', status: 'idle' },
    { key: 'database', group: 'infrastructure', name: 'PostgreSQL', detail: '待检测', status: 'idle' },
    { key: 'file-storage', group: 'infrastructure', name: '文件存储', detail: '待检测', status: 'idle' },
  ]
  const data: CheckItem[] = [
    { key: 'catalog', group: 'data', name: 'Agent 目录', detail: '待检测', status: 'idle' },
    { key: 'conversations', group: 'data', name: '会话存储', detail: '待检测', status: 'idle' },
    { key: 'llm-config', group: 'data', name: 'LLM 配置', detail: '待检测', status: 'idle' },
  ]
  return [...services, ...infra, ...data]
}

function index(key: string): number {
  return checks.value.findIndex(item => item.key === key)
}

function patch(key: string, item: Partial<CheckItem>): void {
  const at = index(key)
  if (at >= 0) checks.value[at] = { ...checks.value[at], ...item }
}

function mapStatus(status?: string): CheckStatus {
  if (status === 'Healthy') return 'ok'
  if (status === 'Degraded') return 'warn'
  return 'error'
}

function toggle(key: string): void {
  expanded.value[key] = !expanded.value[key]
}

async function probeEngine(): Promise<void> {
  let report: HealthReport
  try {
    report = await fetchHealthReport(engineUrl.value)
  } catch (error) {
    patch('engine', { status: 'error', detail: error instanceof Error ? error.message : 'Engine 无法直连' })
    for (const key of ['redis', 'database', 'file-storage']) {
      patch(key, { status: 'na', detail: 'Engine 不可达，无法检测' })
    }
    patch('llm-config', { status: 'na', detail: 'Engine 不可达，无法检测' })
    return
  }
  patch('engine', { status: mapStatus(report.status), detail: `${engineUrl.value} · 总耗时 ${report.totalDurationMs ?? '—'} ms` })
  const known: Record<string, string> = { redis: 'redis', database: 'database', 'file-object-storage': 'file-storage' }
  for (const item of report.items) {
    const target = known[item.key]
    if (target) {
      patch(target, { status: mapStatus(item.status), detail: item.detail || '', latencyMs: item.latencyMs, data: item.data })
    }
  }
  if (!report.items.some(item => item.key === 'file-object-storage')) {
    patch('file-storage', { status: 'na', detail: '未启用（FileAssets.Enabled=false）' })
  }
  const llm = report.items.find(item => item.key === 'llm-connectivity')
  if (llm) {
    patch('llm-config', { status: mapStatus(llm.status), detail: `${llm.detail || ''} · 真实连接测试见 LLM 配置页`, latencyMs: llm.latencyMs, data: llm.data })
  } else {
    patch('llm-config', { status: 'na', detail: '未配置 LLM Provider' })
  }
}

function parseDuration(duration?: string): number | undefined {
  if (!duration) return undefined
  const match = /^([0-9]+):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?$/.exec(duration)
  if (!match) return undefined
  const ms = Number((match[4] || '').padEnd(3, '0'))
  return Math.round(Number(match[1]) * 3600000 + Number(match[2]) * 60000 + Number(match[3]) * 1000 + ms)
}

async function probeRouter(): Promise<void> {
  try {
    const ready = await fetchHealth(routerUrl.value, '/ready')
    const entry = ready.entries['router-ready']
    patch('router', {
      status: mapStatus(ready.status),
      detail: entry?.description || ready.status,
      latencyMs: parseDuration(entry?.duration),
      data: entry?.data,
    })
  } catch (error) {
    patch('router', { status: 'error', detail: error instanceof Error ? error.message : 'Router 不可达' })
  }
}

async function probeGateway(): Promise<void> {
  const attempts: Array<{ key: string; fn: () => Promise<string> }> = [
    {
      key: 'identity',
      fn: async () => {
        const user = await api.getCurrentUser()
        return `${user.userId} · ${user.tenantId || '无租户'} · ${user.isAuthenticated ? '已认证' : '未认证'}`
      },
    },
    { key: 'catalog', fn: async () => `${(await api.listAgents()).length} 个可见 Agent` },
    { key: 'conversations', fn: async () => `${(await api.listConversations()).length} 个会话` },
  ]
  await Promise.all(attempts.map(async attempt => {
    const startedAt = performance.now()
    try {
      const detail = await attempt.fn()
      patch(attempt.key, { status: 'ok', detail, latencyMs: Math.round(performance.now() - startedAt) })
    } catch (error) {
      patch(attempt.key, { status: 'error', detail: error instanceof Error ? error.message : '请求失败', latencyMs: Math.round(performance.now() - startedAt) })
    }
  }))
}

async function run(): Promise<void> {
  running.value = true
  checks.value = seeded().map(item => ({ ...item, status: 'running' }))
  try {
    const tasks = [probeEngine(), probeGateway()]
    if (mode.value === 'router') tasks.push(probeRouter())
    await Promise.all(tasks)
  } finally {
    running.value = false
    ranAt.value = new Date().toISOString()
    saveHealthCheckCache(localStorage, { ranAt: ranAt.value, checks: checks.value })
  }
}

onMounted(() => {
  const cache = loadHealthCheckCache(localStorage)
  if (cache) {
    checks.value = mergeChecksWithSeed(seeded(), cache.checks)
    ranAt.value = cache.ranAt
    return
  }
  void run()
})
</script>

<template>
  <div class="health-check">
    <div class="section-heading">
      <div><span class="eyebrow">SYSTEM CHECK</span><h3>平台健康检查</h3><p>从浏览器逐项验证 Engine、基础设施与数据面状态，结果可直接用于联调报告。</p></div>
      <el-button type="primary" :loading="running" @click="run">运行全部</el-button>
    </div>

    <div class="health-banner" :class="overall.status">
      <span class="health-banner-dot" />
      <div><strong>{{ overall.label }}</strong><small>{{ running ? '正在运行检测…' : summary }}{{ ranAt ? ` · 上次运行 ${runAtLabel}` : '' }}</small></div>
    </div>

    <section v-for="group in groupOrder" :key="group" class="health-group">
      <div class="health-group-heading"><span class="eyebrow">{{ groupMeta[group].eyebrow }}</span><h4>{{ groupMeta[group].label }}</h4></div>
      <div class="diagnostic-grid">
        <article v-for="item in checks.filter(item => item.group === group)" :key="item.key" :class="['diagnostic-card', item.status]">
          <div><span class="diagnostic-dot" /><strong>{{ item.name }}</strong><small v-if="item.latencyMs !== undefined">{{ item.latencyMs }} ms</small></div>
          <p>{{ item.detail }}</p>
          <button v-if="item.data && Object.keys(item.data).length" class="health-detail-toggle" @click="toggle(item.key)">
            {{ expanded[item.key] ? '收起' : '明细' }}
          </button>
          <pre v-if="expanded[item.key] && item.data" class="health-detail">{{ JSON.stringify(item.data, null, 2) }}</pre>
        </article>
      </div>
    </section>
  </div>
</template>
