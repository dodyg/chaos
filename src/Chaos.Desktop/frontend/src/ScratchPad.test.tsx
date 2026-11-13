import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { padRpc, tagRpc, type CaptureDraftDto, type CapturePreviewRequest, type PadItemResponse, type PadMakeTaskRequest, type PadSaveRequest } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { ScratchPad, titleFromSelection } = await import("./ScratchPad");
const { DraftRegistry } = await import("./padAutosave");

function note(id: string, markdown: string, draftVersion = "1"): PadItemResponse {
  return { id, markdown, version: "2", draftVersion, archived: false, archivedAtUtc: null, deleted: false, createdAtUtc: "2026-11-01T00:00:00Z", updatedAtUtc: "2026-11-01T00:00:00Z", tagIds: [], tasks: [] };
}

/** A pad API over in-memory notes; `failSaves` makes every autosave fail like a full disk. */
function padApi(notes: PadItemResponse[], options: { failSaves?: boolean } = {}) {
  const saves: PadSaveRequest[] = [];
  const made: PadMakeTaskRequest[] = [];
  const api = {
    ...padRpc,
    list: async () => ({ ok: true, items: notes, error: null }),
    listDeleted: async () => ({ ok: true, items: [], error: null }),
    reviewStatus: async () => ({ ok: true, due: false, lastReviewedAtUtc: null, suggestions: [], error: null }),
    save: async (request: PadSaveRequest) => {
      saves.push(request);
      if (options.failSaves) return { ok: false, item: null, applied: false, error: { kind: "storageFull", message: "The disk is full.", fields: [], correlationId: "c" } };
      const index = notes.findIndex(row => row.id === request.itemId);
      notes[index] = { ...notes[index], markdown: request.markdown, draftVersion: request.clientRevision, version: String(Number(notes[index].version) + 1) };
      return { ok: true, item: notes[index], applied: true, error: null };
    },
    makeTask: async (request: PadMakeTaskRequest) => {
      made.push(request);
      return { ok: true, task: null, error: null };
    },
  } as unknown as typeof padRpc;
  return { api, saves, made };
}

const tagApi = { ...tagRpc, targetTags: async () => ({ ok: true, tagIds: [], error: null }), suggest: async () => ({ ok: true, tags: [], error: null }) } as unknown as typeof tagRpc;

/** Quick add for Make task: "today" in the selection becomes the planned date. */
const captureApi = {
  preview: async (request: CapturePreviewRequest) => {
    const at = request.text.indexOf("today");
    const title = at < 0 ? request.text : request.text.slice(0, at).trim();
    const draft: CaptureDraftDto = { title, notes: null, containerId: null, priority: "Normal", estimateMinutes: null, plannedDate: at < 0 ? null : "2026-11-10", plannedTime: null,
      dueDate: null, dueTime: null, zoneId: "UTC", recurrence: null, reminders: [], tagIds: [], tagPaths: [], waitingOn: null, followUpDate: null, followUpTime: null, subtasks: [], category: null };
    const tokens = at < 0 ? [] : [{ kind: "PlannedDate", start: at, end: at + 5, text: "today", language: "en", state: "Applied", summary: "Planned today" }];
    return { ok: true, isBatch: false, errors: [], capturedAtUtc: null, error: null,
      items: [{ lineNumber: 1, sourceStart: 0, text: request.text, draft, tokens, warnings: [], languages: [], needsTitle: !title, tags: [], containerCandidates: [], firstOccurrence: null }] };
  },
};

function renderPad(api: typeof padRpc, registry: InstanceType<typeof DraftRegistry>, errors: string[] = []) {
  return render(<ScratchPad api={api} tagApi={tagApi} captureApi={captureApi} registry={registry} containers={[{ id: "inbox", name: "Inbox", kind: "Inbox", version: "1", archived: false, deadlineDate: null, defaultCategory: null }]}
    tags={[]} revision={0} focus={null} links={{}} onChanged={() => {}} onOpenTask={() => {}} onError={message => { if (message) errors.push(message); }}/>, { container: dom.window.document.body });
}

test("a selection's first line becomes the quick-add title without list or heading markers", () => {
  assert.equal(titleFromSelection("- [ ] call Sam\nabout the boat"), "call Sam");
  assert.equal(titleFromSelection("## Plan trip"), "Plan trip");
  assert.equal(titleFromSelection("   \n"), "");
});

test("Make task reads the selection with quick add, saves the latest text first, then sends UTF-16 offsets", async () => {
  const notes = [note("n1", "Ideas\n- call 😀 Sam")];
  const { api, saves, made } = padApi(notes);
  const user = userEvent.setup({ document: dom.window.document });
  renderPad(api, new DraftRegistry());

  await user.click(await screen.findByRole("button", { name: "Open note: Ideas" }));
  const area = await screen.findByLabelText("Note") as HTMLTextAreaElement;
  await user.type(area, " today");
  const text = area.value;
  assert.equal(text, "Ideas\n- call 😀 Sam today");
  const quote = "call 😀 Sam today";
  const start = text.indexOf(quote);
  area.setSelectionRange(start, start + quote.length);
  fireEvent.select(area);

  await user.click(screen.getByRole("button", { name: "Make task" }));
  assert.equal((screen.getByLabelText("Make task from selection") as HTMLTextAreaElement).value, quote);
  const title = await screen.findByLabelText("Title") as HTMLInputElement;
  assert.equal(title.value, "call 😀 Sam"); // "today" became the planned date
  await user.click(screen.getByRole("button", { name: "Create task" }));

  await waitFor(() => assert.equal(made.length, 1));
  // Six typed characters: revisions 2..7 from the loaded draft version 1; Make task waited for the last one.
  assert.equal(saves.at(-1)?.clientRevision, "7");
  assert.equal(saves.at(-1)?.markdown, text);
  assert.equal(made[0].sourceRevision, "7");
  assert.equal(made[0].start, start); // the emoji counts as two UTF-16 units, like the store
  assert.equal(made[0].end, start + quote.length);
  assert.equal(made[0].quote, quote);
  assert.equal(made[0].draft?.title, "call 😀 Sam");
  assert.equal(made[0].draft?.plannedDate, "2026-11-10");
  cleanup();
});

test("a failed save survives leaving the pad and is offered for retry or copy on return", async () => {
  const notes = [note("n1", "draft")];
  const { api, saves } = padApi(notes, { failSaves: true });
  const registry = new DraftRegistry();
  const user = userEvent.setup({ document: dom.window.document });
  renderPad(api, registry);

  await user.click(await screen.findByRole("button", { name: "Open note: draft" }));
  const area = await screen.findByLabelText("Note") as HTMLTextAreaElement;
  await user.type(area, " with more");
  assert.equal(await registry.flushAll(), false); // what the app does when the user navigates away
  assert.ok(saves.length > 0);
  cleanup(); // the pad view is gone

  renderPad(api, registry);
  const closed = await screen.findByRole("button", { name: "Open note: draft with more" });
  assert.match(closed.textContent ?? "", /Not saved/);
  await user.click(closed);
  assert.equal((await screen.findByLabelText("Note") as HTMLTextAreaElement).value, "draft with more");
  const alert = screen.getByRole("alert");
  assert.match(alert.textContent ?? "", /The disk is full\./);
  assert.ok(screen.getByRole("button", { name: "Retry" }));
  assert.ok(screen.getByRole("button", { name: "Copy text" }));
  cleanup();
});
