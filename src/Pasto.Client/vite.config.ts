import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'PASTO_');
  const proxy = { '/api': env.PASTO_API_TARGET ?? 'http://127.0.0.1:5080' };
  return { server: { proxy }, preview: { proxy } };
});
