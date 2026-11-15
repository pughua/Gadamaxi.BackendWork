import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 构建产物直接输出到类库 wwwroot，由 csproj 以嵌入资源打进 DLL
export default defineConfig({
  plugins: [vue()],
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true
  },
  server: {
    proxy: {
      '/backendwork-dashboard': {
        target: 'http://127.0.0.1:5099',
        changeOrigin: false
      }
    }
  }
})
