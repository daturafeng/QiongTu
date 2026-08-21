import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

const currentDirectory = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  root: resolve(currentDirectory, "renderer"),
  base: "./",
  plugins: [react()],
  resolve: {
    alias: {
      "@qiongtu/contracts": resolve(currentDirectory, "../../packages/contracts/src/index.ts")
    }
  },
  build: {
    outDir: resolve(currentDirectory, "dist/renderer"),
    emptyOutDir: true
  },
  test: {
    environment: "jsdom",
    setupFiles: [resolve(currentDirectory, "renderer/test/setup.ts")],
    include: [
      "**/*.test.ts",
      "**/*.test.tsx",
      "../main/**/*.test.ts"
    ]
  }
});
