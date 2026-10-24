import { defineConfig } from 'vite';
import {version} from './src/buildVersion.json';
export default defineConfig({define:{__APP_VERSION__:JSON.stringify(version)}, build: { target: 'safari16', sourcemap: false }, test: { environment: 'jsdom', globals: true } } as Parameters<typeof defineConfig>[0]);
