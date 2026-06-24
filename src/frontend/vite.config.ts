import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { Socket } from 'net'
import { Duplex } from 'stream'

// Polyfill for Bun compatibility (Bun does not implement obsolete Socket.prototype.destroySoon)
// and Bun's HTTP server sockets inherit directly from Duplex rather than net.Socket.
const polyfillDestroySoon = function (this: { destroy?: () => void }) {
  if (typeof this.destroy === 'function') {
    this.destroy()
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
if (Socket && !(Socket.prototype as any).destroySoon) {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  ;(Socket.prototype as any).destroySoon = polyfillDestroySoon
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
if (Duplex && !(Duplex.prototype as any).destroySoon) {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  ;(Duplex.prototype as any).destroySoon = polyfillDestroySoon
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
