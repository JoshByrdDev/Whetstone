import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The defaults are the live instance: this dev server on 5173, proxying to the API on 5338. An
// isolated QA copy (data/qa-copy.ps1) runs its own pair and says so through these two variables,
// so a copy's client can never be reading the live board. strictPort for the copy: a Vite that
// quietly moved to the next free port would be one nobody is looking at.
const apiPort = process.env.WHETSTONE_API_PORT || '5338'
const vitePort = process.env.WHETSTONE_VITE_PORT

export default defineConfig({
  plugins: [react()],
  server: {
    port: vitePort ? Number(vitePort) : 5173,
    strictPort: !!vitePort,
    proxy: {
      '/api': {
        target: `http://localhost:${apiPort}`,
        changeOrigin: true,
      },
      '/hub': {
        target: `http://localhost:${apiPort}`,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
})
