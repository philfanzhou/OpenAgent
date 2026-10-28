/// <reference types="vite/client" />

/** Chat 容器启动时由 /runtime-config.js 写入的浏览器端运行时配置。 */
interface OpenAgentRuntimeConfig {
  routerBaseUrl?: string
  engineBaseUrl?: string
  tenantId?: string
}

interface Window {
  __OPENAGENT_RUNTIME_CONFIG__?: OpenAgentRuntimeConfig
}
