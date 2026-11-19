import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { modelledCommands } from "./preview/backend";

// The generated host contract (written by the NeoAstra build next to the generated client). A preview handler whose command
// no longer exists, or was renamed, would never run: the window would get "not modelled" for the real command instead.
const manifest = JSON.parse(readFileSync(new URL("../../obj/neoastra/neoastra.manifest.json", import.meta.url), "utf8")) as {
  services: { name: string; commands: { name: string }[] }[];
};
const real = new Set(manifest.services.flatMap(service => service.commands.map(command => `${service.name}.${command.name}`)));

test("every command the preview answers is a command the desktop host has", () => {
  const stale = modelledCommands().filter(command => !real.has(command));
  assert.ok(modelledCommands().length > 40, "the preview handlers were not found");
  assert.deepEqual(stale, [], "these preview handlers name commands the host does not have; rename or remove them");
});
