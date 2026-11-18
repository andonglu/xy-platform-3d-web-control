import { defineConfig } from 'vite';

// base './' keeps dist/ portable (sub-path hosting and file:// inspection)
export default defineConfig({
  base: './',
  build: { target: 'es2022', outDir: 'dist' },
  server: {
    proxy: {
      '/ws': { target: 'ws://127.0.0.1:8080', ws: true },
      '/health': 'http://127.0.0.1:8080',
    },
  },
});
