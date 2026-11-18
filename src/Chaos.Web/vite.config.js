import { createHash } from "node:crypto";
import { readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const outDir = fileURLToPath(new URL("../Chaos.Server/wwwroot/app", import.meta.url));
const base = "/app/";

async function filesUnder(directory, prefix = "") {
  const entries = await readdir(directory, { withFileTypes: true });
  const paths = await Promise.all(entries.map(entry => {
    const key = prefix + entry.name;
    return entry.isDirectory() ? filesUnder(join(directory, entry.name), key + "/") : key;
  }));
  return paths.flat();
}

export default defineConfig({
  base,
  esbuild: { jsx: "automatic" },
  build: { outDir, emptyOutDir: true },
  plugins: [{
    name: "chaos-offline-shell",
    apply: "build",
    async closeBundle() {
      const assets = (await filesUnder(outDir)).sort();
      const worker = await readFile(new URL("./src/worker.js", import.meta.url), "utf8");
      const hash = createHash("sha256").update(worker);
      for (const asset of assets) {
        hash.update(asset).update(await readFile(join(outDir, asset)));
      }
      const cache = "chaos-shell-" + hash.digest("hex").slice(0, 16);
      const appFiles = assets.map(asset => base + asset);
      await writeFile(join(outDir, "sw.js"),
        `const CACHE = ${JSON.stringify(cache)};\nconst FILES = ${JSON.stringify(appFiles)};\n${worker}`);
    },
  }],
});
