import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import {
  padRpc,
  type CaptureDraftDto,
  type CapturePreviewRequest,
  type PadDrawingData,
  type PadDrawingSaveRequest,
  type PadItemResponse,
  type PadMakeTaskFromRegionRequest,
} from "#neoastra";

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
// jsdom has no canvas: keep its "not implemented" noise out of the output; the component skips painting without a context.
(dom.window.HTMLCanvasElement.prototype as unknown as { getContext: () => null }).getContext = () => null;
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { DrawingNote, strokeSaveDelayMs } = await import("./PadDrawing");
const { parseDrawing } = await import("./drawing");

const item = {
  id: "d1",
  spaceId: "",
  markdown: "",
  version: "1",
  draftVersion: "0",
  archived: false,
  archivedAtUtc: null,
  deleted: false,
  createdAtUtc: "",
  updatedAtUtc: "",
  tagIds: [],
  tasks: [],
  kind: "drawing",
  previewAttachmentId: null,
  width: 800,
  height: 600,
} as PadItemResponse;

function drawingApi(options: { failSaves?: boolean; strokes?: string } = {}) {
  const saves: PadDrawingSaveRequest[] = [];
  const made: PadMakeTaskFromRegionRequest[] = [];
  const data = (strokesJson: string, revision: string): PadDrawingData => ({
    item,
    strokesJson,
    width: 800,
    height: 600,
    revision,
    previewAttachmentId: null,
  });
  const api = {
    ...padRpc,
    getDrawing: async () => ({ ok: true, drawing: data(options.strokes ?? '{"v":1,"strokes":[]}', "0"), applied: true, error: null }),
    saveDrawing: async (request: PadDrawingSaveRequest) => {
      saves.push(request);
      if (options.failSaves)
        return {
          ok: false,
          drawing: null,
          applied: false,
          error: { kind: "storageFull", message: "The disk is full.", fields: [], correlationId: "c" },
        };
      return { ok: true, drawing: data(request.strokesJson, request.clientRevision), applied: true, error: null };
    },
    makeTaskFromRegion: async (request: PadMakeTaskFromRegionRequest) => {
      made.push(request);
      return { ok: true, task: null, error: null };
    },
  } as unknown as typeof padRpc;
  return { api, saves, made };
}

const captureApi = {
  preview: async (request: CapturePreviewRequest) => {
    const draft: CaptureDraftDto = {
      title: request.text,
      notes: null,
      containerId: null,
      priority: "Normal",
      estimateMinutes: null,
      plannedDate: null,
      plannedTime: null,
      dueDate: null,
      dueTime: null,
      zoneId: "UTC",
      recurrence: null,
      reminders: [],
      tagIds: [],
      tagPaths: [],
      assigneeIds: [],
    delegateToId: null,
      waitingOn: null,
      followUpDate: null,
      followUpTime: null,
      subtasks: [],
      category: null,
    };
    return {
      ok: true,
      isBatch: false,
      errors: [],
      capturedAtUtc: null,
      error: null,
      items: [
        {
          lineNumber: 1,
          sourceStart: 0,
          text: request.text,
          draft,
          tokens: [],
          warnings: [],
          languages: [],
          needsTitle: !request.text.trim(),
          tags: [],
          containerCandidates: [],
          firstOccurrence: null,
        },
      ],
    };
  },
};

function renderNote(api: typeof padRpc, extra: { onSaved?: () => void; onTaskMade?: (title: string) => void } = {}) {
  return render(
    <DrawingNote
      api={api}
      attachmentApi={undefined}
      item={item}
      containers={[]}
      captureApi={captureApi}
      onSaved={extra.onSaved ?? (() => {})}
      onTaskMade={extra.onTaskMade ?? (() => {})}
      onError={() => {}}
    />,
    { container: dom.window.document.body },
  );
}

/** One pointer gesture on the canvas, in drawing units (jsdom reports a zero-size box, so client coordinates are drawing coordinates). */
function gesture(canvas: Element, points: [number, number][], pointerType = "mouse") {
  const [first, ...rest] = points;
  fireEvent.pointerDown(canvas, {
    clientX: first[0],
    clientY: first[1],
    pointerId: 1,
    pointerType,
    button: 0,
    pressure: pointerType === "pen" ? 0.7 : 0.5,
  });
  for (const [x, y] of rest)
    fireEvent.pointerMove(canvas, { clientX: x, clientY: y, pointerId: 1, pointerType, pressure: pointerType === "pen" ? 0.7 : 0.5 });
  const last = points[points.length - 1];
  fireEvent.pointerUp(canvas, { clientX: last[0], clientY: last[1], pointerId: 1, pointerType });
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test("a mouse stroke is saved with the editor revision and appears as one stroke", async () => {
  const { api, saves } = drawingApi();
  renderNote(api);
  const canvas = await screen.findByRole("img", { name: /Drawing note: 0 strokes/ });
  gesture(canvas, [
    [10, 10],
    [60, 40],
    [120, 90],
  ]);
  await screen.findByRole("img", { name: /Drawing note: 1 stroke\./ });
  assert.equal(screen.getByRole("status").textContent, "Unsaved changes");
  await waitFor(() => assert.equal(saves.length, 1), { timeout: strokeSaveDelayMs + 1500 });
  assert.equal(saves[0].clientRevision, "1");
  const stored = parseDrawing(saves[0].strokesJson);
  assert.equal(stored.strokes.length, 1);
  assert.equal(stored.strokes[0].tool, "pen");
  assert.equal(stored.strokes[0].points.length, 3, "the first point plus two moves");
  await waitFor(() => assert.equal(screen.getByRole("status").textContent, "Saved"));
  cleanup();
});

test("pen pressure is kept and the highlighter and colour choices apply to the next stroke", async () => {
  const { api, saves } = drawingApi();
  const user = userEvent.setup({ document: dom.window.document });
  renderNote(api);
  const canvas = await screen.findByRole("img", { name: /Drawing note/ });
  await user.click(screen.getByRole("button", { name: "Highlighter" }));
  await user.click(screen.getByRole("button", { name: "Red" }));
  await user.click(screen.getByRole("button", { name: "Bold" }));
  gesture(
    canvas,
    [
      [5, 5],
      [50, 5],
    ],
    "pen",
  );
  await waitFor(() => assert.equal(saves.length, 1), { timeout: strokeSaveDelayMs + 1500 });
  const stroke = parseDrawing(saves[0].strokesJson).strokes[0];
  assert.equal(stroke.tool, "highlighter");
  assert.equal(stroke.color, "#dc2626");
  assert.equal(stroke.size, 12);
  assert.equal(stroke.points[0][2], 0.7);
  cleanup();
});

test("the eraser removes the stroke under it as one undoable step, and undo and redo save newer revisions", async () => {
  const { api, saves } = drawingApi();
  const user = userEvent.setup({ document: dom.window.document });
  renderNote(api);
  const canvas = await screen.findByRole("img", { name: /Drawing note/ });
  gesture(canvas, [
    [0, 100],
    [200, 100],
  ]);
  gesture(canvas, [
    [0, 300],
    [200, 300],
  ]);
  await screen.findByRole("img", { name: /2 strokes/ });
  await user.click(screen.getByRole("button", { name: "Eraser" }));
  gesture(canvas, [[100, 102]]);
  await screen.findByRole("img", { name: /1 stroke\./ });
  await user.click(screen.getByRole("button", { name: "Undo" }));
  await screen.findByRole("img", { name: /2 strokes/ });
  await user.click(screen.getByRole("button", { name: "Redo" }));
  await screen.findByRole("img", { name: /1 stroke\./ });
  await waitFor(() => assert.equal(screen.getByRole("status").textContent, "Saved"), { timeout: strokeSaveDelayMs + 1500 });
  const last = saves[saves.length - 1];
  assert.equal(Number(last.clientRevision), 5, "two strokes, an erase, an undo and a redo");
  assert.equal(parseDrawing(last.strokesJson).strokes.length, 1);
  assert.deepEqual(
    saves.map((save) => Number(save.clientRevision)),
    [...saves.map((save) => Number(save.clientRevision))].sort((a, b) => a - b),
    "revisions only go up",
  );
  cleanup();
});

test("a failed save keeps the strokes on the canvas and offers a retry that stores them", async () => {
  const state = { fail: true };
  const base = drawingApi();
  const api = {
    ...base.api,
    saveDrawing: async (request: PadDrawingSaveRequest) =>
      state.fail
        ? {
            ok: false,
            drawing: null,
            applied: false,
            error: { kind: "storageFull", message: "The disk is full.", fields: [], correlationId: "c" },
          }
        : base.api.saveDrawing(request),
  } as unknown as typeof padRpc;
  const user = userEvent.setup({ document: dom.window.document });
  renderNote(api);
  const canvas = await screen.findByRole("img", { name: /Drawing note/ });
  gesture(canvas, [
    [10, 10],
    [40, 40],
  ]);
  const alert = await waitFor(() => screen.getByRole("alert"), { timeout: strokeSaveDelayMs + 1500 });
  assert.match(alert.textContent ?? "", /disk is full/);
  assert.ok(screen.getByRole("img", { name: /1 stroke\./ }));
  state.fail = false;
  await user.click(screen.getByRole("button", { name: /Retry/ }));
  await waitFor(() => assert.equal(screen.getByRole("status").textContent, "Saved"));
  assert.equal(base.saves.length, 1);
  cleanup();
});

test("selecting a part and choosing Make task saves first, then sends the region in drawing units", async () => {
  const { api, saves, made } = drawingApi();
  const user = userEvent.setup({ document: dom.window.document });
  const taskMade: string[] = [];
  renderNote(api, { onTaskMade: (title) => taskMade.push(title) });
  const canvas = await screen.findByRole("img", { name: /Drawing note/ });
  gesture(canvas, [
    [10, 10],
    [300, 200],
  ]);
  await user.click(screen.getByRole("button", { name: /Select a part/ }));
  assert.equal(screen.queryByRole("button", { name: /Make task/ }), null, "nothing is selected yet");
  gesture(canvas, [
    [400, 300],
    [100, 50],
  ]);
  await user.click(await screen.findByRole("button", { name: /Make task/ }));
  const box = await screen.findByRole("textbox", { name: /Make task from the selected part/ });
  assert.equal((box as HTMLTextAreaElement).value, "", "no handwriting recognition: the title starts empty");
  await user.type(box, "Fix the corner");
  await waitFor(() => assert.equal((screen.getByLabelText("Title") as HTMLInputElement).value, "Fix the corner"));
  await user.click(await screen.findByRole("button", { name: "Create task" }));
  await waitFor(() => assert.equal(made.length, 1));
  assert.equal(saves.length >= 1, true, "the strokes were stored before the task was made");
  assert.deepEqual(made[0].region, { x: 100, y: 50, w: 300, h: 250 });
  assert.equal(made[0].sourceRevision, "1");
  assert.equal(made[0].draft?.title, "Fix the corner");
  await waitFor(() => assert.deepEqual(taskMade, ["Fix the corner"]));
  cleanup();
});

test("a stray click with the select tool is not a selection", async () => {
  const { api } = drawingApi();
  const user = userEvent.setup({ document: dom.window.document });
  renderNote(api);
  const canvas = await screen.findByRole("img", { name: /Drawing note/ });
  await user.click(screen.getByRole("button", { name: /Select a part/ }));
  gesture(canvas, [
    [100, 100],
    [103, 102],
  ]);
  assert.equal(screen.queryByRole("button", { name: /Make task/ }), null);
  await sleep(50);
  cleanup();
});
