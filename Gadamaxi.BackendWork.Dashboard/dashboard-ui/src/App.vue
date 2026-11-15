<template>
  <n-config-provider :theme="null">
    <n-dialog-provider>
      <n-message-provider>
        <n-layout style="height: 100vh">
        <n-layout-header bordered style="height: 52px; display: flex; align-items: center; padding: 0 20px; gap: 18px">
          <b>BackendWork Dashboard</b>
          <n-menu mode="horizontal" :value="route.path" :options="menuOptions" @update:value="p => router.push(p)" style="flex: 1" />
          <n-button quaternary size="small" @click="refreshAll">刷新</n-button>
        </n-layout-header>
        <n-layout-content style="padding: 16px 20px; height: calc(100vh - 52px); overflow: auto">
          <router-view />
        </n-layout-content>
        </n-layout>
      </n-message-provider>
    </n-dialog-provider>
  </n-config-provider>
</template>

<script setup>
import { h, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NTag } from 'naive-ui'

const route = useRoute()
const router = useRouter()
const reloadKey = ref(0)
const menuOptions = [
  { label: '总览', key: '/overview' },
  { label: '后台任务', key: '/tasks' },
  { label: '批量任务', key: '/batches' },
  { label: '并发执行', key: '/concurrency' },
  { label: '周期任务', key: '/recurring' },
  { label: 'Saga 事务', key: '/sagas' },
  { label: '集群节点', key: '/nodes' }
]
function refreshAll() {
  reloadKey.value++
  window.dispatchEvent(new CustomEvent('dashboard-refresh'))
}
</script>

<style>
body { font-family: system-ui, 'Segoe UI', 'Microsoft YaHei', sans-serif; }
.card-metric { text-align: center; }
</style>
