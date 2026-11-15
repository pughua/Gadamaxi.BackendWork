// 面板 API 客户端：统一前缀、错误与未登录处理
const BASE = '/backendwork-dashboard/api'

export class ApiError extends Error {
  constructor(status, code, message) {
    super(message)
    this.status = status
    this.code = code
  }
}

async function request(method, path, body) {
  const resp = await fetch(BASE + path, {
    method,
    credentials: 'include',
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined
  })

  // 未认证时宿主可能 302 到登录页，fetch 跟随后拿到的是 HTML
  const text = await resp.text()
  let json = null
  try { json = JSON.parse(text) } catch {
    throw new ApiError(resp.status, 'NOT_AUTHENTICATED', '未登录或登录已过期，请先登录宿主应用后再打开面板。')
  }
  if (!resp.ok) {
    const err = json && json.error ? json.error : {}
    throw new ApiError(resp.status, err.code || 'UNKNOWN', err.message || resp.statusText)
  }
  return json
}

export const api = {
  get: (path) => request('GET', path),
  post: (path) => request('POST', path)
}

// 数据库统一存 UTC；无时区后缀的字符串视为 UTC 时间，
// 解析后按浏览器本地时区格式化为 yyyy-MM-dd HH:mm:ss 显示
export function fmtTime(v) {
  if (!v) return '-'
  let s = String(v).trim()
  if (!/([Zz]|[+-]\d{2}:?\d{2})$/.test(s)) s += 'Z'
  const dt = new Date(s)
  if (isNaN(dt.getTime())) return String(v)
  const p = n => String(n).padStart(2, '0')
  return `${dt.getFullYear()}-${p(dt.getMonth() + 1)}-${p(dt.getDate())} ` +
    `${p(dt.getHours())}:${p(dt.getMinutes())}:${p(dt.getSeconds())}`
}
