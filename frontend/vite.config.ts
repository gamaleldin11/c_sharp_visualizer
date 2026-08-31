import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const api = process.env.CSVIZ_API ?? 'http://127.0.0.1:5069';
const proxy = {
  '/api': { target: api, changeOrigin: true },
  '/healthz': { target: api, changeOrigin: true },
};

export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      output: {
        // Monaco is ~1.5MB of the bundle and is not needed to render a trace, so keep it in
        // its own chunk: the app shell and the views load without waiting on the editor.
        manualChunks(id) {
          if (id.includes('monaco-editor')) return 'monaco';
          if (id.includes('elkjs')) return 'elk';
          if (id.includes('@xyflow')) return 'flow';
          return undefined;
        },
      },
    },
    chunkSizeWarningLimit: 900,
  },
  preview: { port: 5174, proxy },
  server: { port: 5173, proxy },
});
