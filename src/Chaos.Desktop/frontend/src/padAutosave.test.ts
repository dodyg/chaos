import { test } from "node:test";
import assert from "node:assert/strict";
import { DraftRegistry, DraftSaver, type SaveOutcome, type Scheduler } from "./padAutosave";

/** Fake time: timers run only when the test advances the clock. */
function fakeScheduler() {
  let now = 0;
  let next = 0;
  const timers = new Map<number, { at: number; callback: () => void }>();
  const scheduler: Scheduler = {
    set: (callback, ms) => { const id = ++next; timers.set(id, { at: now + ms, callback }); return id; },
    clear: handle => { timers.delete(handle as number); },
  };
  function advance(ms: number) {
    now += ms;
    for (const [id, timer] of [...timers].sort((a, b) => a[1].at - b[1].at)) {
      if (timer.at <= now) { timers.delete(id); timer.callback(); }
    }
  }
  return { scheduler, advance, pending: () => timers.size };
}

/** A controllable store: each save waits until the test resolves it. */
function fakeStore() {
  const calls: { revision: number; text: string; resolve: (outcome: SaveOutcome) => void }[] = [];
  let stored = { revision: 0, text: "" };
  const save = (revision: number, text: string) => new Promise<SaveOutcome>(resolve => calls.push({ revision, text, resolve }));
  function answer(index: number, ok = true) {
    const call = calls[index];
    if (!ok) { call.resolve({ ok: false, message: "Disk is full." }); return; }
    if (call.revision > stored.revision) stored = { revision: call.revision, text: call.text };
    call.resolve({ ok: true, draftVersion: stored.revision, version: String(stored.revision + 1), markdown: stored.text });
  }
  return { save, calls, answer, stored: () => stored };
}

const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test("typing is debounced into one save of the latest text after 500 ms of idle", async () => {
  const time = fakeScheduler();
  const store = fakeStore();
  const saver = new DraftSaver({ itemId: "n", text: "", revision: 0, version: "1", save: store.save, scheduler: time.scheduler });

  saver.edit("h"); time.advance(200);
  saver.edit("he"); time.advance(200);
  saver.edit("hello");
  assert.equal(store.calls.length, 0);
  assert.equal(saver.snapshot.status, "pending");
  time.advance(499);
  assert.equal(store.calls.length, 0);
  time.advance(1);
  assert.equal(store.calls.length, 1);
  assert.deepEqual({ revision: store.calls[0].revision, text: store.calls[0].text }, { revision: 3, text: "hello" });
  assert.equal(saver.snapshot.status, "saving");

  store.answer(0); await settle();
  assert.equal(saver.snapshot.status, "saved");
  assert.equal(saver.dirty, false);
  assert.equal(saver.snapshot.version, "4");
});

test("edits during an in-flight save go out next with a newer revision; flush waits for both", async () => {
  const time = fakeScheduler();
  const store = fakeStore();
  const saver = new DraftSaver({ itemId: "n", text: "", revision: 5, version: "1", save: store.save, scheduler: time.scheduler });

  saver.edit("first");
  const flushed = saver.flush();
  assert.equal(store.calls.length, 1);
  saver.edit("first and second"); // typed while the first save is in flight
  const again = saver.flush();      // e.g. blur: shares the running flush, no parallel request
  assert.equal(store.calls.length, 1);

  store.answer(0); await settle();
  assert.equal(store.calls.length, 2);
  assert.deepEqual({ revision: store.calls[1].revision, text: store.calls[1].text }, { revision: 7, text: "first and second" });
  store.answer(1);
  assert.equal(await flushed, true);
  assert.equal(await again, true);
  assert.deepEqual(store.stored(), { revision: 7, text: "first and second" });
  assert.equal(time.pending(), 0);
});

test("a failed save keeps the text, reports Failed, and a retry saves it", async () => {
  const time = fakeScheduler();
  const store = fakeStore();
  const saver = new DraftSaver({ itemId: "n", text: "start", revision: 1, version: "1", save: store.save, scheduler: time.scheduler });
  const statuses: string[] = [];
  saver.subscribe(snapshot => statuses.push(snapshot.status));

  saver.edit("important words");
  const first = saver.flush();
  store.answer(0, false);
  assert.equal(await first, false);
  assert.equal(saver.snapshot.status, "failed");
  assert.equal(saver.snapshot.error, "Disk is full.");
  assert.equal(saver.snapshot.text, "important words");
  assert.equal(saver.dirty, true);

  saver.edit("important words!"); // still shown as failed until a save succeeds
  assert.equal(saver.snapshot.status, "failed");
  const retry = saver.flush();
  store.answer(1);
  assert.equal(await retry, true);
  assert.equal(saver.snapshot.status, "saved");
  assert.deepEqual(store.stored(), { revision: 3, text: "important words!" });
  assert.ok(statuses.includes("failed") && statuses.at(-1) === "saved");
});

test("a store answer older than the sent revision is not taken as saved", async () => {
  const saver = new DraftSaver({ itemId: "n", text: "", revision: 0, version: "1", scheduler: fakeScheduler().scheduler,
    save: async () => ({ ok: true, draftVersion: 0, version: "1", markdown: "" }) });
  saver.edit("x");
  assert.equal(await saver.flush(), false);
  assert.equal(saver.snapshot.status, "failed");
  assert.equal(saver.dirty, true);
});

test("the registry keeps unsaved drafts across navigation and flushes them all", async () => {
  const time = fakeScheduler();
  let fail = true;
  const saved: string[] = [];
  const registry = new DraftRegistry();
  const options = (itemId: string) => ({ itemId, text: "", revision: 0, version: "1", scheduler: time.scheduler,
    save: async (revision: number, text: string): Promise<SaveOutcome> => { if (fail) return { ok: false, message: "offline" }; saved.push(text); return { ok: true, draftVersion: revision, version: "2", markdown: text }; } });
  registry.getOrCreate(options("a")).edit("note a");
  registry.getOrCreate(options("b")).edit("note b");
  assert.equal(registry.dirty, true);

  assert.equal(await registry.flushAll(), false); // leaving the pad while saving fails
  registry.forget("a");                            // a dirty draft is never dropped
  assert.equal(registry.get("a")?.snapshot.text, "note a");
  assert.equal(registry.failed.length, 2);

  fail = false;
  assert.equal(await registry.flushAll(), true);
  assert.deepEqual(saved.sort(), ["note a", "note b"]);
  assert.equal(registry.dirty, false);
  registry.forget("a");
  assert.equal(registry.get("a"), undefined);
});
