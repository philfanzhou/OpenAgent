import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
// Geist fonts (SIL OFL, commercial-free)
import '@fontsource/geist/400.css'
import '@fontsource/geist/500.css'
import '@fontsource/geist/600.css'
import '@fontsource/geist/700.css'
import '@fontsource/geist-mono/400.css'
import '@fontsource/geist-mono/500.css'
import './theme.css'
import './workspace.css'
import App from './App.vue'

createApp(App).use(ElementPlus).mount('#app')
