import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { Socket } from 'net'

// Polyfill for Bun compatibility (Bun does not implement obsolete Socket.prototype.destroySoon)
if (Socket && !Socket.prototype.destroySoon) {
  Socket.prototype.destroySoon = function (this: Socket) {
    this.destroy()
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5000',
        ws: true,
        changeOrigin: true,
      },
    },
  },
})
