import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { CaptureDraftDto, CaptureItemDto, CapturePreviewRequest, CapturePreviewResponse, CaptureTokenDto, ContainerResponse } from "#neoastra";

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
const { QuickAdd, keepExclusions } = await import("./QuickAdd");

const containers: ContainerResponse[] = [
  { id: "inbox", name: "Inbox", kind: "Inbox", version: "1", archived: false, deadlineDate: null },
  { id: "home", name: "Home", kind: "List", version: "1", archived: false, deadlineDate: null },
];

const blank: CaptureDraftDto = { title: "", notes: null, containerId: null, priority: "Normal", estimateMinutes: null, plannedDate: null, plannedTime: null, dueDate: null, dueTime: null,
  zoneId: "UTC", recurrence: null, reminders: [], tagIds: [], tagPaths: [], waitingOn: null, followUpDate: null, followUpTime: null, subtasks: [] };

function item(text: string, draft: Partial<CaptureDraftDto>, tokens: CaptureTokenDto[] = [], lineNumber = 1, sourceStart = 0): CaptureItemDto {
  return { lineNumber, sourceStart, text, draft: { ...blank, ...draft }, tokens, warnings: [], languages: tokens.some(t => t.language) ? ["en"] : [], needsTitle: !draft.title, tags: [], containerCandidates: [], firstOccurrence: null };
}

const ok = (items: CaptureItemDto[], isBatch = false, errors: { lineNumber: number; message: string }[] = []): CapturePreviewResponse =>
  ({ ok: true, isBatch, items, errors, capturedAtUtc: "2026-11-10T09:00:00Z", error: null });

/** A tiny stand-in for the C# parser: "tomorrow" is recognized unless excluded; every line is a task. */
function fakePreview(requests: CapturePreviewRequest[]) {
  return {
    preview: async (request: CapturePreviewRequest) => {
      requests.push(request);
      const lines = request.text.split("\n").filter(line => line.trim());
      const items = lines.map((line, index) => {
        const sourceStart = request.text.indexOf(line);
        const at = line.indexOf("tomorrow");
        const excluded = request.excluded?.some(span => span.start === sourceStart + at);
        if (at < 0 || excluded) return item(line, { title: line.trim() }, [], index + 1, sourceStart);
        const token: CaptureTokenDto = { kind: "PlannedDate", start: at, end: at + 8, text: "tomorrow", language: "en", state: "Applied", summary: "Planned Wed 11 Nov 2026" };
        return item(line, { title: (line.slice(0, at) + line.slice(at + 8)).trim(), plannedDate: "2026-11-11" }, [token], index + 1, sourceStart);
      });
      return ok(items, lines.length > 1);
    },
  };
}

test("keepExclusions drops an exclusion once its text moved or changed", () => {
  const exclusions = [{ start: 5, end: 11, text: "Friday" }, { start: 0, end: 4, text: "Meet" }];
  assert.deepEqual(keepExclusions(exclusions, "Meet Friday"), exclusions);
  assert.deepEqual(keepExclusions(exclusions, "Meet on Friday"), [exclusions[1]]);
});

test("typing shows the recognized date, and saving sends the displayed fields once", async () => {
  const requests: CapturePreviewRequest[] = [];
  const saved: { drafts: CaptureDraftDto[]; operation: string }[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<QuickAdd api={fakePreview(requests)} containers={containers} defaultContainerId="home" label="Add a task"
    onSubmit={async (drafts, operation) => { saved.push({ drafts, operation }); return null; }}/>, { container: dom.window.document.body });

  await user.type(screen.getByLabelText("Add a task"), "Call plumber tomorrow");
  const chip = await screen.findByRole("button", { name: /planned\s*tomorrow/i });
  assert.equal(chip.getAttribute("aria-pressed"), "true");
  const title = await screen.findByLabelText("Title") as HTMLInputElement;
  assert.equal(title.value, "Call plumber");
  assert.ok(requests.length < "Call plumber tomorrow".length, "previews are debounced");

  await user.clear(title);
  await user.type(title, "Call the plumber");
  await user.click(screen.getByRole("button", { name: "Add task" }));
  await waitFor(() => assert.equal(saved.length, 1));
  assert.equal(saved[0].drafts[0].title, "Call the plumber");
  assert.equal(saved[0].drafts[0].plannedDate, "2026-11-11");
  assert.equal(saved[0].drafts[0].containerId, "home"); // no list named: the current list
  assert.equal((screen.getByLabelText("Add a task") as HTMLTextAreaElement).value, "");
  cleanup();
});

test("clicking a recognized part keeps it as plain text in the next preview", async () => {
  const requests: CapturePreviewRequest[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<QuickAdd api={fakePreview(requests)} containers={containers} defaultContainerId="" label="Add a task" onSubmit={async () => null}/>, { container: dom.window.document.body });

  await user.type(screen.getByLabelText("Add a task"), "Meet tomorrow");
  await user.click(await screen.findByRole("button", { name: /planned\s*tomorrow/i }));
  await waitFor(() => assert.deepEqual(requests.at(-1)?.excluded, [{ start: 5, end: 13 }]));
  assert.equal((await screen.findByLabelText("Title") as HTMLInputElement).value, "Meet tomorrow");
  assert.ok(screen.getByRole("button", { name: "“tomorrow” as text" }));
  cleanup();
});

test("a slow preview for older text never replaces the preview of newer text", async () => {
  let release: (() => void) | null = null;
  const api = {
    preview: async (request: CapturePreviewRequest) => {
      if (request.text === "Old") await new Promise<void>(resolve => { release = resolve; });
      return ok([item(request.text, { title: request.text })]);
    },
  };
  render(<QuickAdd api={api} containers={containers} defaultContainerId="" label="Add a task" onSubmit={async () => null}/>, { container: dom.window.document.body });
  const input = screen.getByLabelText("Add a task");
  fireEvent.change(input, { target: { value: "Old" } });
  await waitFor(() => assert.ok(release));
  fireEvent.change(input, { target: { value: "New" } });
  await waitFor(async () => assert.equal((await screen.findByLabelText("Title") as HTMLInputElement).value, "New"));
  release!();
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal((screen.getByLabelText("Title") as HTMLInputElement).value, "New");
  cleanup();
});

test("a pasted list with a problem line saves nothing; a valid list saves every task at once", async () => {
  const saved: CaptureDraftDto[][] = [];
  let withError = true;
  const api = {
    preview: async (request: CapturePreviewRequest) => withError
      ? ok([item("Pack", { title: "Pack" })], true, [{ lineNumber: 3, message: "Line 3 is indented deeper than a subtask." }])
      : ok([item("Pack", { title: "Pack", subtasks: [{ title: "socks", done: false }] }), item("Taxi", { title: "Taxi" }, [], 3, request.text.indexOf("Taxi"))], true),
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<QuickAdd api={api} containers={containers} defaultContainerId="" label="Add a task" onSubmit={async drafts => { saved.push(drafts); return null; }}/>, { container: dom.window.document.body });
  const input = screen.getByLabelText("Add a task");

  fireEvent.change(input, { target: { value: "Pack\n  socks\n      tape" } });
  assert.match((await screen.findByRole("alert")).textContent ?? "", /Line 3 is indented deeper/);
  assert.equal((screen.getByRole("button", { name: /^Add/ }) as HTMLButtonElement).disabled, true);

  withError = false;
  fireEvent.change(input, { target: { value: "Pack\n  socks\nTaxi" } });
  await user.click(await screen.findByRole("button", { name: "Add 2 tasks" }));
  await waitFor(() => assert.equal(saved.length, 1));
  assert.deepEqual(saved[0].map(draft => draft.title), ["Pack", "Taxi"]);
  assert.deepEqual(saved[0][0].subtasks, [{ title: "socks", done: false }]);
  cleanup();
});

test("retrying a failed save reuses the same operation ID", async () => {
  const operations: string[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<QuickAdd api={fakePreview([])} containers={containers} defaultContainerId="" label="Add a task"
    onSubmit={async (_, operation) => { operations.push(operation); return operations.length === 1 ? "Storage is busy. Try again." : null; }}/>, { container: dom.window.document.body });

  await user.type(screen.getByLabelText("Add a task"), "Water plants");
  await screen.findByLabelText("Title");
  await user.click(screen.getByRole("button", { name: "Add task" }));
  assert.match((await screen.findByText("Storage is busy. Try again.")).textContent ?? "", /busy/);
  await user.click(screen.getByRole("button", { name: "Add task" }));
  await waitFor(() => assert.equal(operations.length, 2));
  assert.equal(operations[0], operations[1]);
  cleanup();
});
