import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

/**
 * Browser preview of the UI without the desktop host: the generated RPC client is kept, and
 * `@neoastra/client` is swapped for an in-memory backend with sample data (src/preview).
 */
export default defineConfig({
  resolve: {
    alias: {
      "#neoastra": fileURLToPath(new URL("../obj/neoastra/neoastra.ts", import.meta.url)),
      "@neoastra/client": fileURLToPath(new URL("./src/preview/client.ts", import.meta.url)),
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  plugins: [react(), tailwindcss()],
  server: { fs: { allow: [".."] }, host: "127.0.0.1", strictPort: true, port: 5174 },
});
