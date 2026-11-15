<template>
  <n-spin :show="loading">
    <n-h3>在线 {{ onlineCount }} / 注册 {{ (d || []).length }} 台</n-h3>
    <n-data-table :columns="cols" :data="d || []" size="small" :row-key="r => r.serverName" />
  </n-spin>
</template>

<script setup>
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import { NTag } from 'naive-ui'
import { api, fmtTime } from '../api.js'

const d = ref(null)
const loading = ref(false)
const onlineCount = computed(() => (d.value || []).filter(n => n.online).length)

const cols = [
  { title: '节点', key: 'serverName' },
  {
    title: '状态', key: 'online', width: 90,
    render: r => h(NTag, { size: 'small', type: r.online ? 'success' : 'error' }, { default: () => r.online ? '在线' : '失联' })
  },
  { title: '启动时间', key: 'startedUtcTime', render: r => fmtTime(r.startedUtcTime), width: 165 },
  { title: '最后心跳', key: 'lastHeartbeatUtcTime', render: r => fmtTime(r.lastHeartbeatUtcTime), width: 165 },
  { title: '监听队列', key: 'queues', render: r => (r.queues || []).join(', ') || '-' },
  { title: '运行中', key: 'processing', width: 80 }
]

async function load() {
  loading.value = true
  try { d.value = (await api.get('/nodes')).data } finally { loading.value = false }
}
onMounted(() => { load(); timer = setInterval(load, 10000) })
let timer
onUnmounted(() => clearInterval(timer))
window.addEventListener('dashboard-refresh', load)
</script>
