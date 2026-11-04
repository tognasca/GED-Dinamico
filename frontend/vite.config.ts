import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');

  const apiTarget =
    env.GED_API_TARGET || 'http://127.0.0.1:9192';

  return {
    plugins: [react()],

    server: {
      host: '0.0.0.0',
      port: 8087,
      strictPort: true,

      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
          secure: false,

          configure(proxy) {
            proxy.on('error', (error, request) => {
              console.error(
                `[GED PROXY] ${request.method} ${request.url}`,
                error.message
              );
            });

            proxy.on('proxyReq', (_proxyReq, request) => {
              console.log(
                `[GED PROXY] ${request.method} ${request.url} -> ${apiTarget}`
              );
            });

            proxy.on('proxyRes', (response, request) => {
              console.log(
                `[GED API] ${request.method} ${request.url}: ${response.statusCode}`
              );
            });
          },
        },
      },
    },
  };
});