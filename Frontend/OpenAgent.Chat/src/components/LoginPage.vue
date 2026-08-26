<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { AuthConfig, ConnectionMode } from '../types'

const props = defineProps<{
  authConfig: AuthConfig | null
  loading: boolean
  error?: string
  reason?: string
  connectionMode: ConnectionMode
  routerUrl: string
  engineUrl: string
  tenantId: string
}>()

const emit = defineEmits<{
  basicLogin: [credentials: { username: string; password: string }]
  oidcLogin: []
  retry: []
  updateConnection: [value: { mode: ConnectionMode; routerUrl: string; engineUrl: string; tenantId: string }]
}>()

const username = ref('')
const password = ref('')
const showPassword = ref(false)
const mode = ref(props.connectionMode)
const router = ref(props.routerUrl)
const engine = ref(props.engineUrl)
const tenant = ref(props.tenantId)
const showConnection = ref(false)
const usernameInput = ref<HTMLInputElement>()
const isBasic = computed(() => props.authConfig?.mode === 'Basic')
const isJwtBearer = computed(() => props.authConfig?.mode === 'JwtBearer')
const isOidc = computed(() => isJwtBearer.value && props.authConfig?.keycloak?.enabled !== false)
const isTenantEnabled = computed(() => props.authConfig?.tenant?.enabled !== false)

watch(() => props.loading, async (loading, wasLoading) => {
  if (!loading && wasLoading && isBasic.value) {
    password.value = ''
    await nextTick()
    usernameInput.value?.focus()
  }
})

function applyConnection(): void {
  emit('updateConnection', {
    mode: mode.value,
    routerUrl: router.value,
    engineUrl: engine.value,
    tenantId: tenant.value,
  })
  showConnection.value = false
}

function submit(): void {
  if (props.loading || !username.value.trim() || !password.value) return
  const credentials = { username: username.value.trim(), password: password.value }
  emit('basicLogin', credentials)
  password.value = ''
}
</script>

<template>
  <main class="login-page">
    <section class="login-brand" aria-label="OpenAgent account access">
      <div class="login-brand-mark" aria-hidden="true">
        <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
          <circle cx="10" cy="11" r="2.5" stroke="currentColor" stroke-width="2"/>
          <circle cx="22" cy="11" r="2.5" stroke="currentColor" stroke-width="2"/>
          <circle cx="16" cy="22" r="2.5" stroke="currentColor" stroke-width="2"/>
          <path d="M10 11L16 22M22 11L16 22M10 11L22 11" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" opacity=".5"/>
        </svg>
      </div>
      <span class="eyebrow">OPENAGENT PLATFORM</span>
      <h1>登录工作台</h1>
      <p>恢复你的会话，并通过统一身份边界访问 Agent 服务。</p>
    </section>

    <section class="login-panel" aria-labelledby="login-title" :aria-busy="props.loading">
      <header>
        <div>
          <span class="eyebrow">SECURE ACCESS</span>
          <h2 id="login-title">欢迎回来</h2>
        </div>
        <button class="connection-link" type="button" :aria-expanded="showConnection" @click="showConnection = !showConnection">
          连接设置
        </button>
      </header>

      <div v-if="showConnection" class="login-connection" aria-label="连接设置">
        <label>连接模式
          <select v-model="mode">
            <option value="router">Router（推荐）</option>
            <option value="engine">Engine 直连（仅开发）</option>
          </select>
        </label>
        <label>Router 地址<input v-model="router" type="url" autocomplete="url" spellcheck="false"></label>
        <label v-if="mode === 'engine'">Engine 地址<input v-model="engine" type="url" autocomplete="url" spellcheck="false"></label>
        <label v-if="isBasic && isTenantEnabled">租户 ID<input v-model="tenant" autocomplete="organization" spellcheck="false"></label>
        <button class="secondary-action" type="button" @click="applyConnection">应用并重新检测</button>
      </div>

      <p v-if="props.reason" class="login-notice" role="status">{{ props.reason }}</p>
      <p v-if="props.error" class="login-error" role="alert" aria-live="assertive">{{ props.error }}</p>

      <form v-if="isBasic" class="login-form-page" @submit.prevent="submit">
        <div class="development-warning" role="note">
          <strong>仅限 Development 联调</strong>
          <span>此方式不校验真实密码，不能用于生产环境。</span>
        </div>
        <label for="login-username">账号</label>
        <input id="login-username" ref="usernameInput" v-model="username" name="username" autocomplete="username" required autofocus>
        <label for="login-password">密码</label>
        <div class="password-control">
          <input id="login-password" v-model="password" name="password" :type="showPassword ? 'text' : 'password'" autocomplete="current-password" required>
          <button type="button" :aria-label="showPassword ? '隐藏密码' : '显示密码'" :aria-pressed="showPassword" @click="showPassword = !showPassword">
            {{ showPassword ? '隐藏' : '显示' }}
          </button>
        </div>
        <button class="primary-action" type="submit" :disabled="props.loading || !username.trim() || !password">
          <span v-if="props.loading" class="button-spinner" aria-hidden="true" />
          {{ props.loading ? '正在登录…' : '登录' }}
        </button>
      </form>

      <div v-else-if="isOidc" class="oidc-login">
        <p>继续后将跳转到企业身份提供方。OpenAgent 只在当前标签页会话中保存访问 token。</p>
        <button class="primary-action" type="button" :disabled="props.loading" @click="emit('oidcLogin')">
          <span v-if="props.loading" class="button-spinner" aria-hidden="true" />
          {{ props.loading ? '正在跳转…' : '使用企业账号继续' }}
        </button>
      </div>

      <div v-else-if="isJwtBearer" class="login-unavailable">
        <p>Keycloak 未启用，请在服务端 Authentication:EnableKeycloak 中开启。</p>
        <button class="primary-action" type="button" :disabled="props.loading" @click="emit('retry')">重新检测</button>
      </div>

      <div v-else class="login-unavailable">
        <p>无法读取服务的认证配置。请检查连接地址和服务状态。</p>
        <button class="primary-action" type="button" :disabled="props.loading" @click="emit('retry')">重新检测</button>
      </div>

      <footer>
        <span>认证仅建立身份；角色、Agent ACL 与租户授权由服务端独立判定。</span>
        <span>凭据和 token 不写入 localStorage。</span>
      </footer>
    </section>
  </main>
</template>

<style scoped>
.login-page { min-height: 100vh; display: grid; grid-template-columns: minmax(300px, .9fr) minmax(420px, 1.1fr); background: var(--bg); overflow: auto; }
.login-brand { display: flex; flex-direction: column; justify-content: center; padding: clamp(40px, 8vw, 120px); color: #eefaf7; background: radial-gradient(circle at 25% 18%, #1a735f 0, #103b32 36%, #10231f 100%); }
.login-brand-mark { display: grid; width: 58px; height: 58px; margin-bottom: 42px; place-items: center; border: 1px solid rgba(255,255,255,.22); border-radius: 18px; background: rgba(255,255,255,.08); }
.login-brand .eyebrow { color: #8fd7c5; }
.login-brand h1 { margin: 12px 0 12px; font-size: clamp(36px, 5vw, 66px); line-height: 1.02; letter-spacing: -.045em; }
.login-brand p { max-width: 440px; margin: 0; color: #b8d1cb; font-size: 16px; }
.login-panel { width: min(460px, calc(100% - 40px)); margin: auto; padding: 36px; border: 1px solid var(--border); border-radius: 18px; background: var(--bg); box-shadow: var(--shadow-lg); }
.login-panel header { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; margin-bottom: 24px; }
.login-panel h2 { margin: 5px 0 0; font-size: 28px; letter-spacing: -.025em; }
.connection-link { padding: 4px 0; border: 0; color: var(--text-muted); background: transparent; cursor: pointer; }
.connection-link:hover, .connection-link:focus-visible { color: var(--accent-hover); }
.login-connection { display: grid; gap: 12px; margin: -6px 0 20px; padding: 16px; border: 1px solid var(--border); border-radius: var(--r-md); background: var(--bg-subtle); }
.login-connection label, .login-form-page > label { display: grid; gap: 6px; color: var(--text-secondary); font-size: 13px; font-weight: 600; }
.login-connection input, .login-connection select, .login-form-page input { width: 100%; min-height: 42px; padding: 9px 11px; border: 1px solid var(--border); border-radius: var(--r-sm); outline: 0; background: var(--bg); }
.login-connection input:focus, .login-connection select:focus, .login-form-page input:focus { border-color: var(--border-focus); box-shadow: 0 0 0 3px var(--accent-soft); }
.login-form-page { display: grid; gap: 9px; }
.development-warning { display: grid; gap: 2px; margin-bottom: 8px; padding: 12px; border: 1px solid var(--warn-border); border-radius: var(--r-sm); color: var(--warn); background: var(--warn-soft); font-size: 12px; }
.password-control { position: relative; }
.password-control input { padding-right: 66px; }
.password-control button { position: absolute; top: 5px; right: 5px; min-height: 32px; padding: 0 9px; border: 0; border-radius: 6px; color: var(--text-muted); background: var(--bg-subtle); cursor: pointer; }
.primary-action, .secondary-action { min-height: 42px; border: 0; border-radius: var(--r-sm); font-weight: 600; cursor: pointer; }
.primary-action { display: inline-flex; align-items: center; justify-content: center; gap: 9px; margin-top: 10px; color: #fff; background: var(--accent); }
.primary-action:hover:not(:disabled) { background: var(--accent-hover); }
.primary-action:disabled { cursor: not-allowed; opacity: .58; }
.secondary-action { border: 1px solid var(--border); color: var(--text); background: var(--bg); }
.login-notice, .login-error { margin: 0 0 16px; padding: 11px 12px; border-radius: var(--r-sm); font-size: 13px; }
.login-notice { border: 1px solid var(--warn-border); color: var(--warn); background: var(--warn-soft); }
.login-error { border: 1px solid var(--danger-border); color: var(--danger); background: var(--danger-soft); }
.oidc-login, .login-unavailable { display: grid; gap: 14px; }
.oidc-login p, .login-unavailable p { margin: 0; color: var(--text-muted); line-height: 1.6; }
.login-panel footer { display: grid; gap: 3px; margin-top: 24px; padding-top: 18px; border-top: 1px solid var(--border); color: var(--text-faint); font-size: 11px; }
.button-spinner { width: 14px; height: 14px; border: 2px solid rgba(255,255,255,.45); border-top-color: #fff; border-radius: 50%; animation: spin .7s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
@media (max-width: 760px) { .login-page { grid-template-columns: 1fr; } .login-brand { min-height: 230px; padding: 34px 24px; } .login-brand-mark { margin-bottom: 22px; } .login-brand h1 { font-size: 38px; } .login-panel { margin: 28px auto; padding: 26px; } }
@media (prefers-reduced-motion: reduce) { .button-spinner { animation: none; } }
</style>
