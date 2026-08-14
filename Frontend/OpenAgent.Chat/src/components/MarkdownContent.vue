<script setup lang="ts">
import { computed } from 'vue'
import { renderMarkdown } from '../markdown'

const props = defineProps<{
  content: string
  streaming?: boolean
}>()

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]!)
}

/** 流式渲染时最多对最近这一段内容做 Markdown 解析，更早内容折叠，避免长文本每次更新都整段重解析导致卡顿。 */
const STREAM_MARKDOWN_WINDOW = 6000

const renderedContent = computed(() => {
  if (!props.streaming) return renderMarkdown(props.content)
  // 流式中：已完成的行按 Markdown 渲染，最后一行（未完成）以纯文本内联渲染并带光标，
  // 让光标紧跟在文本末尾。
  const lastNl = props.content.lastIndexOf('\n')
  const stable = lastNl >= 0 ? props.content.slice(0, lastNl + 1) : ''
  const tail = lastNl >= 0 ? props.content.slice(lastNl + 1) : props.content
  const overWindow = stable.length > STREAM_MARKDOWN_WINDOW
  const renderPart = overWindow ? stable.slice(stable.length - STREAM_MARKDOWN_WINDOW) : stable
  const stableHtml = renderPart ? renderMarkdown(renderPart) : ''
  const collapseNote = overWindow
    ? `<div class="stream-collapsed">… 更早内容已折叠（约 ${Math.ceil(stable.length / 1000)}k 字符）</div>`
    : ''
  return `${collapseNote}${stableHtml}<span class="stream-tail">${escapeHtml(tail)}<span class="stream-caret"></span></span>`
})
</script>

<template>
  <div class="markdown-content" v-html="renderedContent" />
</template>
