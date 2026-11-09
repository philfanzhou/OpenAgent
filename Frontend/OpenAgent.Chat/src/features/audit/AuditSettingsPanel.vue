<script setup lang="ts">
import { computed, ref } from 'vue'
import { getAccessToken, getTokenType, getRouterBaseUrl, getEngineBaseUrl } from '../../shared/api/http'
import { useAuditSettings } from './useAuditSettings'

const { auditBaseUrl, saveAuditBaseUrl, resetAuditBaseUrl, defaultAuditBaseUrl } = useAuditSettings()

const urlDraft = ref(auditBaseUrl.value)
const statusText = ref('')
const statusKind = ref<'idle' | 'ok' | 'warn' | 'error'>('idle')
const opening = ref(false)

/** The token the workspace already holds for the connected OpenAgent endpoint. */
const token = computed(() => getAccessToken())
const tokenType = computed(() => getTokenType() || 'Basic')
const hasToken = computed(() => Boolean(token.value))

const resolveAuditOrigin = () => {
  const raw = (auditBaseUrl.value || defaultAuditBaseUrl).trim()
  try {
    return new URL(raw).origin
  } catch {
    return ''
  }
}

/**
 * Hand the workspace identity to the audit console.
 *
 * The audit plane has no user list of its own: it resolves whoever the token
 * belongs to by asking OpenAgent's /me endpoint. So the only thing this button
 * has to do is carry the token over — the console then signs the user in as
 * themselves, with no typing.
 *
 * The token travels in the URL, which is the mechanism audit-web implements
 * (`/ui/login?token=...`). It is exchanged for a session cookie immediately, so
 * it does not linger in the console afterwards.
 */
const openAuditConsole = async () => {
  statusText.value = ''
  const origin = resolveAuditOrigin()
  if (!origin) {
    statusKind.value = 'error'
    statusText.value = '审计台地址无效，请先在下方填写正确的地址。'
    return
  }
  if (!hasToken.value) {
    statusKind.value = 'error'
    statusText.value = '当前没有 OpenAgent 登录令牌，请先在「连接」中完成登录，再进入审计台。'
    return
  }

  opening.value = true
  try {
    const target = new URL('/ui/login', origin)
    target.searchParams.set('token', token.value)
    window.open(target.toString(), '_blank', 'noopener')
    statusKind.value = 'ok'
    statusText.value = '已在新标签页打开审计台，将使用当前登录身份自动进入。'
  } catch {
    statusKind.value = 'error'
    statusText.value = '打开审计台失败，请检查浏览器是否拦截了弹出窗口。'
  } finally {
    opening.value = false
  }
}

const save = () => {
  const normalized = saveAuditBaseUrl(urlDraft.value)
  urlDraft.value = normalized
  statusKind.value = 'ok'
  statusText.value = '审计台地址已保存。'
}

const reset = () => {
  urlDraft.value = resetAuditBaseUrl()
  statusKind.value = 'idle'
  statusText.value = '已恢复默认地址。'
}

const connectionHint = computed(() => {
  try {
    const connected = getRouterBaseUrl() || getEngineBaseUrl()
    return connected || '未连接'
  } catch {
    return '未连接'
  }
})
</script>

<template>
  <section class="settings-section">
    <div class="section-heading">
      <div>
        <span class="eyebrow">AUDIT PLANE</span>
        <h3>会话审计</h3>
        <p>使用当前已登录的 OpenAgent 身份进入审计台，查看脱敏会话；查看原文需在审计台内单独进行管理员登录。</p>
      </div>
      <div class="section-actions">
        <el-button type="primary" :loading="opening" @click="openAuditConsole">打开审计台</el-button>
      </div>
    </div>

    <el-descriptions :column="2" border class="identity-status" style="margin-bottom: 16px">
      <el-descriptions-item label="当前用户">{{ token ? '已登录（令牌有效）' : '未登录' }}</el-descriptions-item>
      <el-descriptions-item label="令牌类型">{{ tokenType }}</el-descriptions-item>
      <el-descriptions-item label="审计台地址">{{ resolveAuditOrigin() || '未配置' }}</el-descriptions-item>
      <el-descriptions-item label="OpenAgent 连接">{{ connectionHint }}</el-descriptions-item>
    </el-descriptions>

    <el-alert
      v-if="!hasToken"
      type="warning"
      :closable="false"
      show-icon
      title="尚未取得 OpenAgent 登录令牌"
      description="审计台依赖当前登录身份自动进入。请先在「连接」标签页完成登录后再使用本功能。"
      style="margin-bottom: 16px"
    />

    <el-alert
      v-else
      type="info"
      :closable="false"
      show-icon
      title="将以当前身份进入审计台"
      description="点击「打开审计台」会在新标签页中自动登录，无需再次输入账号密码。审计台只显示脱敏内容。"
      style="margin-bottom: 16px"
    />

    <el-form label-position="top" class="connection-form">
      <el-form-item label="审计台地址">
        <el-input v-model="urlDraft" placeholder="http://10.51.4.45:8100" />
        <small class="form-help">
          默认 {{ defaultAuditBaseUrl }}，可通过环境变量 VITE_OPENAGENT_AUDIT_BASE_URL 预置。
        </small>
      </el-form-item>
    </el-form>

    <div class="button-row">
      <el-button type="primary" plain @click="save">保存地址</el-button>
      <el-button @click="reset">恢复默认</el-button>
    </div>

    <p v-if="statusText" class="form-help" :class="{ error: statusKind === 'error' }">{{ statusText }}</p>

    <el-alert
      type="info"
      :closable="false"
      title="跨域说明"
      style="margin-top: 16px"
      description="审计台与工作台不同源时，浏览器会把 OpenAgent 令牌带到审计台地址。审计台用该令牌向 OpenAgent 换取身份后立即转为会话 Cookie，不会把令牌留在页面里。"
    />
  </section>
</template>