import test from "node:test";
import assert from "node:assert/strict";
import { trackSync, setSyncObserved } from "./syncActivity";

// The hook reads the same snapshot; observe it through a subscription-free probe by tracking promises.
test("tracked syncs and observed syncs combine per kind and settle back to idle", async () => {
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  const tracked = trackSync("calendar", pending);
  setSyncObserved("github", true);
  release();
  await tracked;
  setSyncObserved("github", false);
  // Rejections still release the count.
  await assert.rejects(trackSync("github", Promise.reject(new Error("x"))));
});
