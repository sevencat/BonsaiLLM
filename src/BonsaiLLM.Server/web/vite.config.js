import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { quasar, transformAssetUrls } from '@quasar/vite-plugin'
import { viteSingleFile } from 'vite-plugin-singlefile'

// 产物是单个 index.html，内联所有 js/css，供 Server 当嵌入资源用
export default defineConfig({
  plugins: [
    vue({ template: { transformAssetUrls } }),
    quasar({ sassVariables: false }),
    viteSingleFile(),
  ],
  server: {
    port: 5181,
    proxy: {
      '/api': 'http://127.0.0.1:5180',
      '/v1': 'http://127.0.0.1:5180',
    },
  },
  build: {
    target: 'es2020',
    outDir: 'dist',
    emptyOutDir: true,
    cssCodeSplit: false,
    assetsInlineLimit: Number.MAX_SAFE_INTEGER,
    chunkSizeWarningLimit: 4096,
  },
})
