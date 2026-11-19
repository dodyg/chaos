import { test } from "node:test";
import assert from "node:assert/strict";
import { handle } from "./preview/backend";

test("a command the preview does not model fails loudly instead of answering with a guess", async () => {
  const original = console.error;
  const logged: string[] = [];
  console.error = (message: unknown) => { logged.push(String(message)); };
  try {
    const result = await handle("nothing.modelled", {}) as { ok: boolean; error: { message: string } };
    assert.equal(result.ok, false);
    assert.match(result.error.message, /nothing\.modelled/);
    assert.equal(logged.length, 1);
  } finally { console.error = original; }
});
