<template>
  <n-spin :show="loading">
    <n-grid :cols="4" :x-gap="12" :y-gap="12">
      <n-gi><n-card size="small"><n-statistic label="在线节点" :value="d.onlineNodes ?? '-'" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="运行中任务" :value="d.processingTotal ?? '-'" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="队列积压" :value="enqueued ?? '-'" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="今日失败率" :value="failureRate" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="今日成功" :value="d.succeededToday ?? '-'" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="今日失败" :value="d.failedToday ?? '-'" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="活跃 Saga / 卡住" :value="`${d.sagaRunning ?? 0} / ${d.sagaStalled ?? 0}`" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="周期任务（暂停）" :value="`${d.recurringTotal ?? 0}（${d.recurringPaused ?? 0}）`" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="批量任务（进行中）" :value="`${d.batchTotal ?? 0}（${d.batchRunning ?? 0}）`" /></n-card></n-gi>
      <n-gi><n-card size="small"><n-statistic label="批量失败" :value="d.batchFailure ?? '-'" /></n-card></n-gi>
    </n-grid>

    <n-h3 style="margin-top:20px">各状态任务数</n-h3>
    <n-space>
      <n-tag v-for="s in d.stateCounts || []" :key="s.key" size="large" round>
        {{ s.key }}: {{ s.value }}
      </n-tag>
      <n-text v-if="!(d.stateCounts || []).length" depth="3">暂无数据</n-text>
    </n-space>

    <n-h3 style="margin-top:20px">队列深度 / 抢占中</n-h3>
    <n-data-table :columns="queueCols" :data="d.queues || []" size="small" :bordered="false" />
  </n-spin>
</template>

<script setup>
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import { api } from '../api.js'

const d = ref({})
const loading = ref(false)
const enqueued = computed(() => {
  const s = (d.value.stateCounts || []).find(x => x.key === 'enqueued')
  return s ? s.value : (d.value.enqueuedTotal ?? 0)
})
const failureRate = computed(() =>
  d.value.failureRateToday == null ? '-' : (d.value.failureRateToday * 100).toFixed(1) + '%')

const queueCols = [
  { title: '队列', key: 'queue' },
  { title: '深度', key: 'depth' },
  { title: '已被抢占', key: 'fetched' }
]

async function load() {
  loading.value = true
  try { d.value = (await api.get('/overview')).data } finally { loading.value = false }
}
onMounted(() => { load(); timer = setInterval(load, 10000) })
let timer
onUnmounted(() => clearInterval(timer))
window.addEventListener('dashboard-refresh', load)
</script>
