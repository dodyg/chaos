import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { padRpc, tagRpc, attachmentRpc, type AttachmentDto, type PadItemResponse, type PadSaveRequest } from "#neoastra";

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
(dom.window.HTMLCanvasElement.prototype as unknown as { getContext: () => null }).getContext = () => null;
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { ScratchPad } = await import("./ScratchPad");
const { DraftRegistry } = await import("./padAutosave");
type Engine = import("./VoiceRecorder").RecorderEngine;

const base = { archived: false, archivedAtUtc: null, deleted: false, createdAtUtc: "", updatedAtUtc: "", tagIds: [], tasks: [], previewAttachmentId: null, width: 0, height: 0 };
const attachmentId = "11111111-1111-4111-8111-111111111111";

/** A pad and an attachment store in memory. `failUpload` makes the file refuse to store, like a full disk. */
function fakes(options: { failUpload?: boolean } = {}) {
  const notes: PadItemResponse[] = [];
  const deleted: string[] = [];
  const saves: PadSaveRequest[] = [];
  const uploads: { ownerId: string; fileName: string }[] = [];
  const created: { width: number; height: number }[] = [];
  let counter = 0;
  const api = {
    ...padRpc,
    list: async () => ({ ok: true, items: notes.filter(note => !deleted.includes(note.id)), error: null }),
    listDeleted: async () => ({ ok: true, items: [], error: null }),
    reviewStatus: async () => ({ ok: true, due: false, lastReviewedAtUtc: null, suggestions: [], error: null }),
    create: async (request: { markdown: string }) => {
      const note = { ...base, id: `n${++counter}`, kind: "text", markdown: request.markdown, version: "1", draftVersion: "0" } as PadItemResponse;
      notes.push(note);
      return { ok: true, items: [note], error: null };
    },
    createDrawing: async (request: { width: number; height: number }) => {
      created.push({ width: request.width, height: request.height });
      const note = { ...base, id: `d${++counter}`, kind: "drawing", markdown: "", version: "1", draftVersion: "0", width: request.width, height: request.height } as PadItemResponse;
      notes.push(note);
      return { ok: true, items: [note], error: null };
    },
    getDrawing: async (request: { itemId: string }) => ({
      ok: true, applied: true, error: null,
      drawing: { item: notes.find(note => note.id === request.itemId)!, strokesJson: "{\"v\":1,\"strokes\":[]}", width: 1600, height: 1200, revision: "0", previewAttachmentId: null },
    }),
    save: async (request: PadSaveRequest) => {
      saves.push(request);
      const index = notes.findIndex(note => note.id === request.itemId);
      notes[index] = { ...notes[index], markdown: request.markdown, draftVersion: request.clientRevision, version: "2" };
      return { ok: true, item: notes[index], applied: true, error: null };
    },
    delete: async (request: { itemId: string }) => { deleted.push(request.itemId); return { ok: true, items: [], error: null }; },
  } as unknown as typeof padRpc;
  const attachments = {
    ...attachmentRpc,
    begin: async (request: { ownerId: string; fileName: string }) => {
      if (options.failUpload) return { ok: false, uploadId: "", chunkBytes: 0, maxFileBytes: "0", error: { kind: "storageFull", message: "The disk is full.", fields: [], correlationId: "c" } };
      uploads.push({ ownerId: request.ownerId, fileName: request.fileName });
      return { ok: true, uploadId: "u1", chunkBytes: 1024, maxFileBytes: "1000000", error: null };
    },
    chunk: async () => ({ ok: true, error: null }),
    cancel: async () => ({ ok: true, error: null }),
    commit: async () => {
      const fileName = uploads.at(-1)?.fileName ?? "x";
      return { ok: true, error: null, attachment: { id: attachmentId, fileName, preview: fileName.endsWith(".png") ? "image" : "audio" } as AttachmentDto };
    },
  } as unknown as typeof attachmentRpc;
  return { api, attachments, notes, deleted, saves, uploads, created };
}

const tagApi = { ...tagRpc, targetTags: async () => ({ ok: true, tagIds: [], error: null }), suggest: async () => ({ ok: true, tags: [], error: null }) } as unknown as typeof tagRpc;

function renderPad(f: ReturnType<typeof fakes>, errors: string[], recorder?: Engine) {
  return render(<ScratchPad api={f.api} attachmentApi={f.attachments} recorder={recorder} tagApi={tagApi} registry={new DraftRegistry()}
    containers={[]} tags={[]} revision={0} focus={null} links={{}} onChanged={() => {}} onOpenTask={() => {}} onError={message => { if (message) errors.push(message); }}/>, { container: dom.window.document.body });
}

const goodEngine: Engine = {
  supported: () => true,
  start: async () => ({ onLevel: () => {}, stop: async () => ({ blob: new Blob(["abc"], { type: "audio/webm" }), mimeType: "audio/webm" }), cancel: () => {} }),
};

test("New drawing stores an empty drawing at once and opens the canvas", async () => {
  const f = fakes();
  const user = userEvent.setup({ document: dom.window.document });
  const errors: string[] = [];
  renderPad(f, errors);
  await user.click(await screen.findByRole("button", { name: /New drawing/ }));
  await screen.findByRole("img", { name: /Drawing note: 0 strokes/ });
  assert.deepEqual(f.created, [{ width: 1600, height: 1200 }]);
  assert.ok(screen.getByRole("toolbar", { name: "Drawing tools" }));
  cleanup();
});

test("a voice note becomes a note whose first line is the title, with the audio attached and linked", async () => {
  const f = fakes();
  const user = userEvent.setup({ document: dom.window.document });
  renderPad(f, [], goodEngine);
  await user.click(await screen.findByRole("button", { name: /Voice note/ }));
  await user.click(await screen.findByRole("button", { name: /Stop and save/ }));
  await waitFor(() => assert.equal(f.saves.length, 1));
  assert.deepEqual(f.uploads, [{ ownerId: "n1", fileName: "voice-note.webm" }]);
  assert.match(f.saves[0].markdown, new RegExp(`^🎙️ Voice note, \\d\\d:\\d\\d\\n\\n\\[voice-note\\.webm\\]\\(chaos:attachment/${attachmentId}\\)\\n$`));
  assert.equal(f.saves[0].clientRevision, "1");
  cleanup();
});

test("a photo note puts the picture at the top of a new note", async () => {
  const f = fakes();
  renderPad(f, []);
  const input = await screen.findByLabelText("Photo file");
  fireEvent.change(input, { target: { files: [new File(["png"], "dog.png", { type: "image/png" })] } });
  await waitFor(() => assert.equal(f.saves.length, 1));
  assert.deepEqual(f.uploads, [{ ownerId: "n1", fileName: "dog.png" }]);
  assert.match(f.saves[0].markdown, new RegExp(`^!\\[dog\\.png\\]\\(chaos:attachment/${attachmentId}\\)\\n$`));
  cleanup();
});

test("a photo note can be made by pasting an image", async () => {
  const f = fakes();
  renderPad(f, []);
  fireEvent.click(await screen.findByRole("button", { name: /New photo note/ }));
  const box = await screen.findByLabelText("Paste an image here");
  fireEvent.paste(box, { clipboardData: { files: [new File(["png"], "image.png", { type: "image/png" })], types: ["Files"] } });
  await waitFor(() => assert.equal(f.saves.length, 1));
  assert.deepEqual(f.uploads, [{ ownerId: "n1", fileName: "image.png" }]);
  cleanup();
});

test("when the file cannot be stored the half-made note is deleted and the error is shown", async () => {
  const f = fakes({ failUpload: true });
  const errors: string[] = [];
  renderPad(f, errors);
  fireEvent.change(await screen.findByLabelText("Photo file"), { target: { files: [new File(["png"], "dog.png", { type: "image/png" })] } });
  await waitFor(() => assert.equal(errors.length, 1));
  assert.match(errors[0], /disk is full/);
  assert.deepEqual(f.deleted, ["n1"]);
  assert.equal(f.saves.length, 0);
  cleanup();
});
