import { defineConfig, loadEnv } from 'vite';
import type { ProxyOptions } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'PASTO_');
  const target = env.PASTO_API_TARGET ?? 'http://127.0.0.1:5080';
  const hub: ProxyOptions = {
    target,
    ws: true,
    bypass(request) {
      if (!request.url) return;
      const url = new URL(request.url, 'http://localhost');
      const tokens = url.searchParams.getAll('access_token');
      if (tokens.length === 0) return;
      // Vite 會記錄部分代理網址。瀏覽器傳輸的憑證改由標頭轉交。
      url.searchParams.delete('access_token');
      request.url = url.pathname + url.search;
      if (url.pathname === '/hubs/room' && request.method === 'GET' && tokens.length === 1 && /^[A-Za-z0-9_-]+$/.test(tokens[0])
        && (request.headers.upgrade?.toLowerCase() === 'websocket' || request.headers.accept === 'text/event-stream')) {
        request.headers.authorization ??= `Bearer ${tokens[0]}`;
      }
    },
  };
  const proxy = { '/api': target, '/hubs/room': hub };
  return { server: { proxy }, preview: { proxy } };
});
