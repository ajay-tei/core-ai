import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import path from 'path';
import { defineConfig } from 'vite';

// VITE_BASE_PATH: optional subpath prefix when served behind a reverse proxy
// e.g. VITE_BASE_PATH=/beta/tei-ai → all asset URLs become /beta/tei-ai/assets/...
// Must NOT have a trailing slash. Leave empty (default) for root deployment.
const rawBase = (process.env.VITE_BASE_PATH ?? '').replace(/\/+$/, '');
const base = rawBase ? rawBase + '/' : '/';

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  base,
  server: { port: 5173 },
  build: {
    rollupOptions: {
      input: {
        main: path.resolve(__dirname, 'index.html'),
        widget: path.resolve(__dirname, 'widget.html'),
      },
      output: {
        // All static files under /assets/ — reverse proxies need only one extra routing rule
        entryFileNames: 'assets/[name]-[hash].js',
        chunkFileNames: 'assets/chunk-[hash].js',
        assetFileNames: 'assets/[name]-[hash][extname]',
      },
    },
  },
});
