<template>
  <n-space vertical>
    <n-data-table remote :columns="cols" :data="rows" :loading="loading" size="small" :row-key="r => r.id"
      :pagination="pagination" @update:page="p => { pagination.page = p; load() }"
      @update:page-size="s => { pagination.pageSize = s; pagination.page = 1; load() }" />

    <n-modal v-model:show="showHistory" preset="card" style="width: 700px" :title="'执行历史：' + (current?.name || '')">
      <n-space vertical>
        <n-text depth="3">按周期任务 ID（{{ current?.id }}）关联，非按任务名匹配</n-text>
        <n-data-table remote :columns="historyCols" :data="history" size="small"
          :pagination="historyPagination" @update:page="p => { historyPagination.page = p; loadHistory(current) }" />
      </n-space>
    </n-modal>
  </n-space>
</template>

<script setup>
import { h, onMounted, reactive, ref } from 'vue'
import { NButton, NTag, useMessage } from 'naive-ui'
import { api, fmtTime } from '../api.js'

const message = useMessage()
const rows = ref([])
const loading = ref(false)
const current = ref(null)
const history = ref([])
const showHistory = ref(false)
const pagination = reactive({ page: 1, pageSize: 20, itemCount: 0, showQuickJumper: true,
  showSizePicker: true, pageSizes: [10, 20, 50, 100], prefix: () => `共 ${pagination.itemCount} 条` })
const historyPagination = reactive({ page: 1, pageSize: 20, itemCount: 0, showQuickJumper: true })

const cols = [
  { title: 'ID', key: 'id', width: 160, ellipsis: { tooltip: true } },
  { title: '名称', key: 'name' },
  { title: 'Cron', key: 'cron', width: 110 },
  { title: '队列', key: 'queue', width: 100 },
  {
    title: '状态', key: 'paused', width: 90,
    render: r => h(NTag, { size: 'small', type: r.paused ? 'warning' : 'success' }, { default: () => r.paused ? '已暂停' : '运行中' })
  },
  { title: '下次执行', key: 'nextExecuteUtcTime', render: r => fmtTime(r.nextExecuteUtcTime), width: 165 },
  { title: '上次执行', key: 'lastExecuteUtcTime', render: r => fmtTime(r.lastExecuteUtcTime), width: 165 },
  {
    title: '操作', width: 260,
    render: r => h('n-space', null, {
      default: () => [
        h(NButton, { size: 'tiny', onClick: () => act('trigger', r) }, { default: () => '触发' }),
        h(NButton, { size: 'tiny', onClick: () => act('pause', r), disabled: r.paused }, { default: () => '暂停' }),
        h(NButton, { size: 'tiny', onClick: () => act('resume', r), disabled: !r.paused }, { default: () => '恢复' }),
        h(NButton, { size: 'tiny', quaternary: true, onClick: () => openHistory(r) }, { default: () => '历史' })
      ]
    })
  }
]

const historyCols = [
  { title: '任务 ID', key: 'id', width: 220, ellipsis: { tooltip: true } },
  { title: '状态', key: 'state', width: 100 },
  { title: '创建时间', key: 'createUtcTime', render: r => fmtTime(r.createUtcTime), width: 170 }
]

async function load() {
  loading.value = true
  try {
    const qs = new URLSearchParams({ page: String(pagination.page), pageSize: String(pagination.pageSize) })
    const resp = await api.get(`/recurring?${qs}`)
    rows.value = resp.data
    pagination.itemCount = resp.meta?.total ?? resp.data.length
  } catch (e) { message.error(e.message) } finally { loading.value = false }
}

async function act(action, r) {
  try {
    const resp = await api.post(`/recurring/${r.id}/${action}`)
    message.success(resp.data.message)
    load()
  } catch (e) { message.error(`${e.code}: ${e.message}`) }
}

async function openHistory(r) {
  current.value = r
  historyPagination.page = 1
  showHistory.value = true
  await loadHistory(r)
}

async function loadHistory(r) {
  if (!r) return
  try {
    const qs = new URLSearchParams({ page: String(historyPagination.page), pageSize: String(historyPagination.pageSize) })
    const resp = await api.get(`/recurring/${r.id}/history?${qs}`)
    history.value = resp.data
    historyPagination.itemCount = resp.meta?.total ?? resp.data.length
  } catch (e) { message.error(e.message) }
}

onMounted(load)
window.addEventListener('dashboard-refresh', load)
</script>
