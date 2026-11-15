<template>
  <n-space vertical>
    <n-space align="center">
      <n-select v-model:value="status" :options="statusOptions" clearable placeholder="状态" style="width: 180px" @update:value="reload" />
      <n-input v-model:value="name" placeholder="名称包含" clearable style="width: 220px" @keyup.enter="reload" />
      <n-button size="small" type="primary" @click="reload">查询</n-button>
      <n-text depth="3">共 {{ total }} 条</n-text>
    </n-space>

    <n-data-table remote :columns="cols" :data="rows" :loading="loading" size="small" :row-key="r => r.batchId"
      :pagination="pagination" @update:page="p => { pagination.page = p; load() }" />

    <n-modal v-model:show="showDetail" preset="card" style="width: 960px" :title="'批量详情：' + (current?.name || '')">
      <n-space vertical>
        <n-descriptions bordered :column="2" size="small" v-if="detail">
          <n-descriptions-item label="批量 ID"><span style="word-break:break-all">{{ detail.batch.batchId }}</span></n-descriptions-item>
          <n-descriptions-item label="状态">{{ detail.batch.status }}</n-descriptions-item>
          <n-descriptions-item label="子任务数">{{ detail.batch.count }}</n-descriptions-item>
          <n-descriptions-item label="已处理">{{ detail.batch.dealCount }}</n-descriptions-item>
          <n-descriptions-item label="重试次数">{{ detail.batch.retryCount }}</n-descriptions-item>
          <n-descriptions-item label="父批量">{{ detail.batch.parentBatchId || '-' }}</n-descriptions-item>
          <n-descriptions-item label="创建时间">{{ fmtTime(detail.batch.createUtcTime) }}</n-descriptions-item>
          <n-descriptions-item label="过期时间">{{ fmtTime(detail.batch.expireUtcTime) }}</n-descriptions-item>
          <n-descriptions-item label="消息" :span="2">{{ detail.batch.message || '-' }}</n-descriptions-item>
        </n-descriptions>
        <n-data-table :columns="itemCols" :data="detail?.items || []" size="small" max-height="420px" :row-key="r => r.id" />
      </n-space>
    </n-modal>
  </n-space>
</template>

<script setup>
import { h, onMounted, onUnmounted, reactive, ref } from 'vue'
import { NButton, NTag, useMessage, useDialog } from 'naive-ui'
import { api, fmtTime } from '../api.js'

const message = useMessage()
const dialog = useDialog()
const rows = ref([])
const total = ref(0)
const pagination = reactive({ page: 1, pageSize: 20, itemCount: 0, showQuickJumper: true,
  prefix: () => `共 ${total.value} 条` })
const status = ref(null)
const name = ref('')
const loading = ref(false)
const showDetail = ref(false)
const current = ref(null)
const detail = ref(null)

const statusOptions = [
  { label: '等待中 Awaiting', value: 'Awaiting' },
  { label: '初始化 Init', value: 'Init' },
  { label: '执行中 Processing', value: 'Processing' },
  { label: '失败 Failure', value: 'Failure' },
  { label: '成功 Success', value: 'Success' },
  { label: '无状态 None', value: 'None' }
]

function statusType(s) {
  return ({ Success: 'success', Failure: 'error', Processing: 'info', Awaiting: 'warning', Init: 'warning' })[s] || 'default'
}

const cols = [
  { title: '批量 ID', key: 'batchId', width: 200, ellipsis: { tooltip: true } },
  { title: '名称', key: 'name' },
  {
    title: '状态', key: 'status', width: 110,
    render: r => h(NTag, { size: 'small', type: statusType(r.status) }, { default: () => r.status })
  },
  { title: '进度', key: 'count', width: 110, render: r => `${r.dealCount ?? 0} / ${r.count ?? 0}` },
  { title: '重试', key: 'retryCount', width: 70 },
  { title: '消息', key: 'message', ellipsis: { tooltip: true } },
  { title: '创建时间', key: 'createUtcTime', width: 165, render: r => fmtTime(r.createUtcTime) },
  { title: '更新时间', key: 'updateUtcTime', width: 165, render: r => fmtTime(r.updateUtcTime) },
  {
    title: '操作', width: 140,
    render: r => h('n-space', { size: 4 }, {
      default: () => [
        h(NButton, { size: 'tiny', onClick: () => openDetail(r) }, { default: () => '详情' }),
        h(NButton, {
          size: 'tiny', type: 'warning',
          disabled: r.status !== 'Failure',
          onClick: () => retry(r)
        }, { default: () => '重试' })
      ]
    })
  }
]

const itemCols = [
  { title: '子任务', key: 'name' },
  {
    title: '状态', key: 'status', width: 110,
    render: r => h(NTag, { size: 'small', type: statusType(r.status) }, { default: () => r.status })
  },
  { title: '关联 WorkId', key: 'workId', width: 200, ellipsis: { tooltip: true }, render: r => r.workId || '-' },
  { title: '消息', key: 'message', ellipsis: { tooltip: true } },
  { title: '创建时间', key: 'createUtcTime', width: 165, render: r => fmtTime(r.createUtcTime) }
]

async function load() {
  loading.value = true
  try {
    const qs = new URLSearchParams({ page: String(pagination.page), pageSize: String(pagination.pageSize) })
    if (status.value) qs.set('status', status.value)
    if (name.value) qs.set('name', name.value)
    const resp = await api.get(`/batches?${qs}`)
    rows.value = resp.data
    total.value = resp.meta?.total ?? resp.data.length
    pagination.itemCount = total.value
  } catch (e) { message.error(e.message) } finally { loading.value = false }
}

function reload() {
  pagination.page = 1
  load()
}

function retry(r) {
  dialog.warning({
    title: '重试批量任务',
    content: `确认重试「${r.name}」？只会重新执行未成功的子任务，已成功的子任务不会重复执行。`,
    positiveText: '确认重试',
    negativeText: '取消',
    onPositiveClick: async () => {
      try {
        const resp = await api.post(`/batches/${r.batchId}/retry`)
        message.success(resp.data.message)
        load()
      } catch (e) { message.error(`${e.code}: ${e.message}`) }
    }
  })
}

async function openDetail(r) {
  current.value = r
  try {
    detail.value = (await api.get(`/batches/${r.batchId}`)).data
    showDetail.value = true
  } catch (e) { message.error(e.message) }
}

onMounted(() => { load(); timer = setInterval(load, 5000) })
let timer
onUnmounted(() => clearInterval(timer))
window.addEventListener('dashboard-refresh', load)
</script>
