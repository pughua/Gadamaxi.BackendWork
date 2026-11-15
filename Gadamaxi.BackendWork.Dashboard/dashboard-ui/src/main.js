import { createApp } from 'vue'
import { createRouter, createWebHashHistory } from 'vue-router'
import naive from 'naive-ui'
import App from './App.vue'
import Overview from './views/Overview.vue'
import Tasks from './views/Tasks.vue'
import Batches from './views/Batches.vue'
import Concurrency from './views/Concurrency.vue'
import Recurring from './views/Recurring.vue'
import Sagas from './views/Sagas.vue'
import Nodes from './views/Nodes.vue'

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/overview' },
    { path: '/overview', component: Overview },
    { path: '/tasks', component: Tasks },
    { path: '/batches', component: Batches },
    { path: '/concurrency', component: Concurrency },
    { path: '/recurring', component: Recurring },
    { path: '/sagas', component: Sagas },
    { path: '/nodes', component: Nodes }
  ]
})

createApp(App).use(router).use(naive).mount('#app')
