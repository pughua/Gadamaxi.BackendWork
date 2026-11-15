# dashboard-ui — BackendWork 监控面板前端

Vue 3 + Naive UI SPA，构建产物输出到 `../wwwroot`，由 `Gadamaxi.BackendWork.Dashboard.csproj`
以嵌入资源打进 DLL（宿主无需 wwwroot、无需 UseStaticFiles）。

## 构建

```bash
npm install
npm run build          # 产物 → ../wwwroot，然后重新编译类库即可
```

开发模式（热更新，需宿主已在 5099 端口运行）：

```bash
npm run dev            # vite 代理 /backendwork-dashboard → http://127.0.0.1:5099
```

## 页面

| 路由 | 视图 | 数据源 |
|---|---|---|
| /overview | 总览指标、状态分布、队列深度 | GET /api/overview |
| /tasks | 任务列表/详情（状态历史=重试链、操作记录）、重试/取消 | /api/tasks* |
| /concurrency | 运行中任务（4s 轮询，慢任务标红）、队列状态 | /api/concurrency |
| /recurring | 周期任务、手动触发/暂停/恢复、执行历史 | /api/recurring* |
| /sagas | Saga 列表（卡住标记）、步骤时间线 | /api/sagas* |
| /nodes | 集群节点心跳/在线状态 | /api/nodes |

## 认证

面板不做登录：请求 `credentials: 'include'` 透传宿主 Cookie 认证；
未认证时（响应非 JSON）统一提示"请先登录宿主应用"。

## 时间口径

框架的 `CreateUtcTime` 等字段实际以**服务器本地时间**落库（存量口径），
前端按原样展示，不做时区换算。
