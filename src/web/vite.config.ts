import { defineConfig } from "vitest/config";
import { readFileSync } from "node:fs";

const proxy = (target: string, prefix: string) => ({
  target,
  changeOrigin: false,
  rewrite: (path: string) => path.replace(prefix, ""),
});
export default defineConfig({
  server: {
    port: Number(process.env.PORT ?? 7440),
    strictPort: true,
    https: process.env.DEV_HTTPS === "true" ? {
      cert: readFileSync(process.env.DEV_CERT ?? "../../.runtime/localhost.crt"),
      key: readFileSync(process.env.DEV_KEY ?? "../../.runtime/localhost.key"),
    } : undefined,
    proxy: {
      "/connections/a": proxy(process.env.API_A ?? "https://localhost:7441", "/connections/a"),
      "/connections/b": proxy(process.env.API_B ?? "https://localhost:7442", "/connections/b"),
    },
  },
  test: { include: ["../../tests/web/**/*.test.ts"] },
});
