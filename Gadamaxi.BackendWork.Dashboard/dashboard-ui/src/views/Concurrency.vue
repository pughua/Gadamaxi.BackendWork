<template>
  <n-spin :show="loading">
    <n-h3>Worker 占用（按节点）</n-h3>
    <n-data-table :columns="nodeCols" :data="d.nodes || []" size="small" />
    <n-h3 style="margin-top:16px">运行中任务（超过 {{ 60 }}s 标红）</n-h3>
    <n-data-table :columns="procCols" :data="d.processing || []" size="small" />
    <n-h3 style="margin-top:16px">队列状态</n-h3>
    <n-data-table :columns="queueCols" :data="d.queues || []" size="small" />
  </n-spin>
</template>

<script setup>
import { h, onMounted, onUnmounted, ref } from 'vue'
import { api, fmtTime } from '../api.js'

const d = ref({})
const loading = ref(false)

const nodeCols = [
  { title: '节点', key: 'node' },
  { title: '运行中', key: 'processing' }
]
const queueCols = [
  { title: '队列', key: 'queue' },
  { title: '深度', key: 'depth' },
  { title: '已被抢占', key: 'fetched' }
]
const procCols = [
  { title: '任务 ID', key: 'workId', width: 220, ellipsis: { tooltip: true } },
  { title: '名称', key: 'name' },
  { title: '节点', key: 'node', width: 140 },
  { title: '开始时间', key: 'startTime', render: r => fmtTime(r.startTime), width: 170 },
  {
    title: '已运行(s)', key: 'runSeconds', width: 110,
    render: r => h('span', { style: r.runSeconds >= 60 ? 'color:#d03050;font-weight:600' : '' }, String(r.runSeconds))
  }
]

async function load() {
  loading.value = true
  try { d.value = (await api.get('/concurrency')).data } finally { loading.value = false }
}
onMounted(() => { load(); timer = setInterval(load, 4000) })
let timer
onUnmounted(() => clearInterval(timer))
window.addEventListener('dashboard-refresh', load)
</script>
