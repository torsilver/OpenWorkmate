import { fileURLToPath, URL } from 'node:url'

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { copyFile } from "wpsjs/vite_plugins"

/** 与 patch 后的 wpsjs 一致：为 wps / et / wpp 各写一条 publish 记录且 url 不同，WPS 才会在文字、表格、演示中同时加载同一套页面。 */
function wpsAddonScopePlugin() {
  return {
    name: 'wps-addon-scope',
    configureServer(server) {
      server.middlewares.use((req, _res, next) => {
        const raw = req.url || ''
        const q = raw.indexOf('?')
        const pathOnly = q >= 0 ? raw.slice(0, q) : raw
        const m = pathOnly.match(/^\/wps-addon-scope\/(wps|et|wpp)(\/.*)?$/i)
        if (!m) return next()
        const rest = m[2] && m[2].length > 1 ? m[2] : '/'
        req.url = rest + (q >= 0 ? raw.slice(q) : '')
        next()
      })
    }
  }
}

// https://vitejs.dev/config/
export default defineConfig({
  base:'./',
  plugins: [
    copyFile({
      src: 'manifest.xml',
      dest: 'manifest.xml',
    }),
    wpsAddonScopePlugin(),
    vue()
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  server: {
    host: '0.0.0.0'
  }
})
