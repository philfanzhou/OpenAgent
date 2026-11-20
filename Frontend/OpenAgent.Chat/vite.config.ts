import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
  },
  build: {
    rollupOptions: {
      output: {
        // 分离第三方库：业务代码改动不再使 vendor 缓存失效，首屏解析也更快。
        manualChunks: {
          vue: ['vue'],
          'element-plus': ['element-plus'],
          markdown: ['markdown-it'],
        },
      },
    },
  },
})
