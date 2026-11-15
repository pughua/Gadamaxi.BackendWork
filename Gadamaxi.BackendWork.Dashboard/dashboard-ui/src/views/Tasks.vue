<template>
  <n-space vertical>
    <n-space align="center">
      <n-select v-model:value="state" :options="stateOptions" size="small" style="width: 160px" clearable placeholder="状态" />
      <n-input v-model:value="name" size="small" style="width: 200px" placeholder="按名称搜索" clearable
        @keydown.enter="reload" />
      <n-input v-model:value="workId" size="small" style="width: 240px" placeholder="按任务 ID 过滤（支持片段）" clearable
        @keydown.enter="reload" />
      <n-button size="small" @click="reload">查询</n-button>
      <n-button size="small" @click="reset">重置</n-button>
    </n-space>

    <n-data-table remote :columns="cols" :data="rows" :loading="loading" size="small" :row-key="r => r.id"
      :pagination="pagination" @update:page="p => { pagination.page = p; load() }"
      @update:page-size="s => { pagination.pageSize = s; reload() }" />

    <n-modal v-model:show="showDetail" preset="card" style="width: 720px" :title="'任务详情 ' + (detail?.id || '')">
      <n-descriptions v-if="detail" :column="2" size="small" bordered>
        <n-descriptions-item label="名称">{{ detail.name }}</n-descriptions-item>
        <n-descriptions-item label="状态">{{ detail.state }}</n-descriptions-item>
        <n-descriptions-item label="队列">{{ detail.queue || '-' }}</n-descriptions-item>
        <n-descriptions-item label="执行节点">{{ detail.node || '-' }}</n-descriptions-item>
        <n-descriptions-item label="创建时间">{{ fmtTime(detail.createUtcTime) }}</n-descriptions-item>
        <n-descriptions-item label="过期时间">{{ fmtTime(detail.expireUtcTime) }}</n-descriptions-item>
      </n-descriptions>
      <n-h4>状态历史（重试链）{{ historyCount ? `（${historyCount} 条）` : '' }}</n-h4>
      <div class="history-scroll">
        <n-timeline v-if="historyCount">
          <n-timeline-item v-for="s in (detail.stateHistory || [])" :key="s.name + s.createUtcTime"
            :title="s.name" :content="s.info" :time="fmtTime(s.createUtcTime)" />
        </n-timeline>
        <n-empty v-else description="无状态历史" size="small" />
      </div>
      <template #footer>
        <n-space>
          <n-button size="small" @click="doWrite('retry', '确认重试该任务？')">重试</n-button>
          <n-button size="small" @click="doWrite('cancel', '确认取消该任务？')">取消</n-button>
        </n-space>
      </template>
    </n-modal>
  </n-space>
</template>

<script setup>
import { computed, h, onMounted, reactive, ref } from 'vue'
import { NButton, NTag, useMessage } from 'naive-ui'
import { api, fmtTime } from '../api.js'

const message = useMessage()
const rows = ref([])
const loading = ref(false)
const state = ref(null)
const name = ref('')
const workId = ref('')
const detail = ref(null)
const showDetail = ref(false)
const historyCount = computed(() => (detail.value?.stateHistory || []).length)
const pagination = reactive({ page: 1, pageSize: 20, itemCount: 0, showQuickJumper: true,
  showSizePicker: true, pageSizes: [10, 20, 50, 100], prefix: () => `共 ${pagination.itemCount} 条` })

// 首项「全部」：value 为 null，后端收到空即不过滤
const stateOptions = [{ label: '全部', value: null }]
  .concat(['scheduled', 'enqueued', 'processing', 'awaiting', 'succeeded', 'failed', 'deleted']
    .map(s => ({ label: s, value: s })))

function stateTag(state) {
  const color = { succeeded: 'success', failed: 'error', processing: 'info', enqueued: 'warning' }[state]
  return h(NTag, { size: 'small', type: color || 'default' }, { default: () => state })
}

const cols = [
  { title: 'ID', key: 'id', width: 200, ellipsis: { tooltip: true } },
  { title: '名称', key: 'name' },
  { title: '状态', key: 'state', render: r => stateTag(r.state), width: 110 },
  { title: '队列', key: 'queue', width: 110 },
  { title: '节点', key: 'node', width: 130 },
  { title: '创建时间', key: 'createUtcTime', render: r => fmtTime(r.createUtcTime), width: 170 },
  { title: '', width: 80, render: r => h(NButton, { size: 'tiny', onClick: () => openDetail(r.id) }, { default: () => '详情' }) }
]

async function load() {
  loading.value = true
  try {
    const qs = new URLSearchParams({ page: pagination.page, pageSize: pagination.pageSize })
    if (state.value) qs.set('state', state.value)
    if (name.value) qs.set('name', name.value)
    if (workId.value) qs.set('id', workId.value.trim())
    const resp = await api.get('/tasks?' + qs)
    rows.value = resp.data
    pagination.itemCount = resp.meta?.total ?? resp.data.length
  } catch (e) { message.error(e.message) } finally { loading.value = false }
}

function reload() {
  pagination.page = 1
  load()
}

function reset() {
  state.value = null
  name.value = ''
  workId.value = ''
  reload()
}

async function openDetail(id) {
  try {
    detail.value = (await api.get('/tasks/' + id)).data
    showDetail.value = true
  } catch (e) { message.error(e.message) }
}

async function doWrite(action, tip) {
  try {
    const resp = await api.post(`/tasks/${detail.value.id}/${action}`)
    message.success(resp.data.message)
    showDetail.value = false
    load()
  } catch (e) { message.error(`${e.code}: ${e.message}`) }
}

onMounted(load)
window.addEventListener('dashboard-refresh', load)
</script>

<style scoped>
/* 状态历史可能很长（重试链），限制高度并由内部滚动，避免详情弹窗被撑高 */
.history-scroll {
  max-height: 260px;
  overflow-y: auto;
  padding: 4px 8px 4px 2px;
  border: 1px solid #eee;
  border-radius: 6px;
}
</style>
