<script setup lang="ts">
import { computed, ref } from 'vue'
import { renderMarkdown } from '../markdown'
import { rewriteMarkdownImages } from '../markdownAssets'

const props = defineProps<{
  content: string
  streaming?: boolean
  /** 同步查找已解析的图片 blob URL；未命中返回 undefined，渲染保留原引用。 */
  resolveImage?: (src: string) => string | undefined
}>()

const contentElement = ref<HTMLElement | null>(null)

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]!)
}

/** 命中缓存的图片引用替换为 blob URL 后再进入 markdown 渲染。 */
function prepare(content: string): string {
  return props.resolveImage ? rewriteMarkdownImages(content, props.resolveImage) : content
}

/** 流式渲染时最多对最近这一段内容做 Markdown 解析，更早内容折叠，避免长文本每次更新都整段重解析导致卡顿。 */
const STREAM_MARKDOWN_WINDOW = 6000

const renderedContent = computed(() => {
  if (!props.streaming) return renderMarkdown(prepare(props.content))
  // 流式中：已完成的行按 Markdown 渲染，最后一行（未完成）以纯文本内联渲染并带光标，
  // 让光标紧跟在文本末尾。
  const lastNl = props.content.lastIndexOf('\n')
  const stable = lastNl >= 0 ? props.content.slice(0, lastNl + 1) : ''
  const tail = lastNl >= 0 ? props.content.slice(lastNl + 1) : props.content
  const overWindow = stable.length > STREAM_MARKDOWN_WINDOW
  const renderPart = overWindow ? stable.slice(stable.length - STREAM_MARKDOWN_WINDOW) : stable
  const stableHtml = renderPart ? renderMarkdown(prepare(renderPart)) : ''
  const collapseNote = overWindow
    ? `<div class="stream-collapsed">… 更早内容已折叠（约 ${Math.ceil(stable.length / 1000)}k 字符）</div>`
    : ''
  return `${collapseNote}${stableHtml}<span class="stream-tail">${escapeHtml(tail)}<span class="stream-caret"></span></span>`
})

async function copyCode(text: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text)
    return
  }

  const input = document.createElement('textarea')
  input.value = text
  input.setAttribute('readonly', '')
  input.style.position = 'fixed'
  input.style.opacity = '0'
  document.body.appendChild(input)
  input.select()
  const copied = document.execCommand('copy')
  input.remove()
  if (!copied) throw new Error('Clipboard is unavailable')
}

async function onCodeAction(event: MouseEvent): Promise<void> {
  const target = event.target instanceof Element ? event.target : null
  const button = target?.closest<HTMLButtonElement>('[data-code-action]')
  if (!button || !contentElement.value?.contains(button)) return
  const block = button.closest<HTMLElement>('[data-code-block]')
  if (!block) return

  if (button.dataset.codeAction === 'wrap') {
    const wrapped = block.classList.toggle('is-wrapped')
    button.setAttribute('aria-pressed', String(wrapped))
    button.textContent = wrapped ? '取消换行' : '自动换行'
    return
  }

  if (button.dataset.codeAction !== 'copy') return
  const code = block.querySelector('pre code')?.textContent ?? ''
  try {
    await copyCode(code)
    button.textContent = '已复制'
    window.setTimeout(() => {
      if (button.isConnected) button.textContent = '复制'
    }, 1500)
  } catch {
    button.textContent = '复制失败'
    window.setTimeout(() => {
      if (button.isConnected) button.textContent = '复制'
    }, 1500)
  }
}
</script>

<template>
  <div ref="contentElement" class="markdown-content" v-html="renderedContent" @click="onCodeAction" />
</template>
