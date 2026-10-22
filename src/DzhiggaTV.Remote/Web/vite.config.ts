import { defineConfig } from 'vite';
export default defineConfig({ build: { target: 'safari16', sourcemap: false }, test: { environment: 'jsdom', globals: true } } as Parameters<typeof defineConfig>[0]);
