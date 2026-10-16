import { defineConfig } from "vite";
import { copyFile } from "node:fs/promises";

export default defineConfig({
  base: "./",
  server: { host: "127.0.0.1" },
  plugins: [{
    name: "copy-third-party-notices",
    async closeBundle() {
      await copyFile(new URL("./THIRD-PARTY-NOTICES.txt", import.meta.url), new URL("./dist/THIRD-PARTY-NOTICES.txt", import.meta.url));
    },
  }],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rollupOptions: { input: "index.html" },
  },
});
