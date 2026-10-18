import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// base: './' so the built site works from any folder on the Vultr server.
export default defineConfig({ plugins: [react()], base: './', server: { port: 5173, host: true } });
