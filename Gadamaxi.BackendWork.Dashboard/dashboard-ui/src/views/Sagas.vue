<template>
  <n-space vertical>
    <n-space align="center">
      <n-select v-model:value="status" :options="statusOptions" size="small" style="width: 160px" clearable placeholder="状态" />
      <n-checkbox v-model:checked="stalledOnly" size="small">仅看卡住</n-checkbox>
      <n-button size="small" @click="load">查询</n-button>
    </n-space>

    <n-data-table remote :columns="cols" :data="rows" :loading="loading" size="small" :row-key="r => r.sagaId"
      :pagination="pagination" @update:page="p => { pagination.page = p; load() }" />

    <n-modal v-model:show="showDetail" preset="card" style="width: 780px" :title="'Saga 详情 ' + (detail?.saga?.sagaId || '')">
      <n-descriptions v-if="detail" :column="2" size="small" bordered>
        <n-descriptions-item label="名称">{{ detail.saga.name }}</n-descriptions-item>
        <n-descriptions-item label="状态">{{ detail.saga.status }}</n-descriptions-item>
        <n-descriptions-item label="步骤进度">{{ detail.saga.currentIndex }} / {{ detail.saga.stepCount }}</n-descriptions-item>
        <n-descriptions-item label="补偿游标">{{ detail.saga.compensationIndex }}</n-descriptions-item>
      </n-descriptions>
      <n-h4>步骤时间线</n-h4>
      <n-steps v-if="detail" :current="detail.saga.currentIndex + 1" size="small" vertical>
        <n-step v-for="s in detail.steps" :key="s.stepId"
          :title="`#${s.index} ${s.name}`"
          :description="`${s.status}${s.message ? ' · ' + s.message : ''} · ${fmtTime(s.updateUtcTime || s.createUtcTime)}`"
          :status="stepStatus(s)" />
      </n-steps>
    </n-modal>
  </n-space>
</template>

<script setup>
import { h, onMounted, reactive, ref } from 'vue'
import { NButton, NTag } from 'naive-ui'
import { api, fmtTime } from '../api.js'

const rows = ref([])
const loading = ref(false)
const status = ref(null)
const stalledOnly = ref(false)
const detail = ref(null)
const showDetail = ref(false)
const pagination = reactive({ page: 1, pageSize: 20, pageCount: 1 })

const statusOptions = ['Running', 'Compensating', 'Completed', 'Compensated', 'Aborted', 'None']
  .map(s => ({ label: s, value: s }))

function statusTag(s) {
  const color = { Completed: 'success', Compensated: 'success', Aborted: 'error', Compensating: 'warning', Running: 'info' }[s]
  return h(NTag, { size: 'small', type: color || 'default' }, { default: () => s })
}

function stepStatus(s) {
  if (s.status === 'Succeeded') return 'finish'
  if (s.status === 'Running') return 'process'
  if (s.status === 'Failed' || s.status === 'CompensationFailed') return 'error'
  return 'wait'
}

const cols = [
  { title: 'Saga ID', key: 'sagaId', width: 200, ellipsis: { tooltip: true } },
  { title: '名称', key: 'name' },
  { title: '状态', key: 'status', render: r => statusTag(r.status), width: 110 },
  { title: '进度', width: 90, render: r => `${r.currentIndex}/${r.stepCount}` },
  {
    title: '卡住', key: 'stalled', width: 70,
    render: r => r.stalled ? h(NTag, { size: 'small', type: 'error' }, { default: () => '卡住' }) : '-'
  },
  { title: '开始时间', key: 'createUtcTime', render: r => fmtTime(r.createUtcTime), width: 165 },
  {
    title: '', width: 80,
    render: r => h(NButton, { size: 'tiny', onClick: () => openDetail(r.sagaId) }, { default: () => '详情' })
  }
]

async function load() {
  loading.value = true
  try {
    const qs = new URLSearchParams({ page: pagination.page, pageSize: pagination.pageSize })
    if (status.value) qs.set('status', status.value)
    if (stalledOnly.value) qs.set('stalled', 'true')
    const resp = await api.get('/sagas?' + qs)
    rows.value = resp.data
    pagination.pageCount = Math.max(1, Math.ceil(resp.meta.total / pagination.pageSize))
  } finally { loading.value = false }
}

async function openDetail(id) {
  try {
    detail.value = (await api.get('/sagas/' + id)).data
    showDetail.value = true
  } catch (e) { console.error(e) }
}

onMounted(load)
window.addEventListener('dashboard-refresh', load)
</script>
