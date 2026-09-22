<script setup lang="ts">
import type { PlanSnapshot } from '../types'

const props = defineProps<{
  plan: PlanSnapshot
  /** 流式进行中：未全部完成的步骤保持活动观感（进行中转圈）。 */
  streaming?: boolean
}>()

function statusClass(status: string): string {
  return status === 'completed' ? 'done' : status === 'in_progress' ? 'running' : 'pending'
}

function statusText(status: string): string {
  return status === 'completed' ? '已完成' : status === 'in_progress' ? '进行中' : '待处理'
}

const allDone = () => props.plan.completed >= props.plan.total
</script>

<template>
  <details class="plan-card" open>
    <summary class="plan-card-head">
      <span class="plan-card-title">任务计划</span>
      <span class="plan-card-progress">{{ plan.completed }}/{{ plan.total }} 已完成</span>
      <span class="plan-card-chevron" aria-hidden="true">›</span>
    </summary>
    <ol class="plan-steps">
      <li
        v-for="(item, index) in plan.plan"
        :key="`${index}-${item.step}`"
        class="plan-step"
        :class="`is-${statusClass(item.status)}`"
      >
        <span class="plan-step-index">{{ index + 1 }}</span>
        <span class="plan-step-text">{{ item.step }}</span>
        <span class="plan-step-status" :class="statusClass(item.status)">
          <span v-if="item.status === 'in_progress' && streaming && !allDone()" class="status-spinner" />
          {{ statusText(item.status) }}
        </span>
      </li>
    </ol>
  </details>
</template>
