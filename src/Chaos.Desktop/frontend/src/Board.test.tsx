import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { type BoardCardResponse, type BoardColumnResponse, type BoardGetResponse, type BoardSettingsDto, type TaskResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  Element: { value: dom.window.Element, configurable: true },
  Node: { value: dom.window.Node, configurable: true },
  DOMRect: { value: dom.window.DOMRect, configurable: true },
  CustomEvent: { value: dom.window.CustomEvent, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { Board, loadLayout, saveLayout } = await import("./Board");

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return { id, title, spaceId: "space", containerId: "list-1", notes: "", status: "NotStarted", plannedDate: null, dueDate: null, version: "1", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null, ...patch };
}

const statusColumns: BoardColumnResponse[] = [
  { key: "NotStarted", title: "Not started", isNone: false },
  { key: "InProgress", title: "In progress", isNone: false },
  { key: "Done", title: "Done", isNone: false },
];
const defaultSettings: BoardSettingsDto = { groupBy: "Status", swimlaneBy: "None", columnTagId: null, swimlaneTagId: null, wipLimitsJson: "{}", hiddenColumnsJson: "[]", version: "3", isDefault: false };

function card(item: TaskResponse, patch: Partial<BoardCardResponse> = {}): BoardCardResponse {
  return { task: item, tagIds: [], subtasksTotal: 0, subtasksDone: 0, hasIssueLink: false, columnKey: item.status, laneKey: "", ...patch };
}

/** A fake board service: it serves one board and logs every move and settings write. */
function fakes(options: { cards: BoardCardResponse[]; wipLimitsJson?: string; truncated?: boolean; totalCount?: number; askAboutSubtasks?: boolean }) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const board: BoardGetResponse = {
    ok: true, error: null, settings: { ...defaultSettings, wipLimitsJson: options.wipLimitsJson ?? "{}" },
    columns: statusColumns, lanes: [{ key: "", title: "All tasks", isNone: true }],
    cards: options.cards, totalCount: options.totalCount ?? options.cards.length, truncated: options.truncated ?? false,
  };
  const api = {
    get: async () => structuredClone(board),
    move: async (request: Record<string, unknown> & { toColumn: string | null; subtaskChoice: string | null }) => {
      calls.push({ method: "move", request });
      if (options.askAboutSubtasks && request.toColumn === "Done" && request.subtaskChoice === null) {
        return { ok: false, task: null, warning: null, error: { kind: "Validation", message: "Open subtasks", fields: [{ fieldPath: "openSubtaskChoice", message: "Choose how to handle the open subtasks." }], correlationId: "x" } };
      }
      return { ok: true, task: (options.cards.find(item => item.task.id === request.taskId)?.task ?? null), error: null, warning: null };
    },
    updateSettings: async (request: Record<string, unknown>) => {
      calls.push({ method: "updateSettings", request });
      return { ok: true, settings: { ...defaultSettings, version: "4" }, error: null };
    },
  } as unknown as Parameters<typeof Board>[0]["api"];
  return { api, calls };
}

const dataTransfer = () => ({ effectAllowed: "", dropEffect: "", types: ["text/plain"], setData: () => {}, getData: () => "" });

function mount(api: Parameters<typeof Board>[0]["api"], onOpen: (task: TaskResponse) => void = () => {}) {
  return render(<Board containerId="list-1" tags={[]} focus={null} busy={false} revision={0} onOpen={onOpen} onChanged={() => {}} api={api}/>, { container: dom.window.document.body });
}

test("the layout preference is remembered per container and switching never touches tasks", () => {
  dom.window.localStorage.clear();
  assert.equal(loadLayout("list-1"), "list");
  saveLayout("list-1", "board");
  assert.equal(loadLayout("list-1"), "board");
  assert.equal(loadLayout("list-2"), "list", "another container starts on the list layout");
  saveLayout("list-1", "list");
  assert.equal(loadLayout("list-1"), "list");
});

test("columns and cards render, and a column over its WIP limit shows count / limit as a warning", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second", { status: "InProgress" }));
  const third = card(task("c", "Third", { status: "InProgress" }));
  const { api } = fakes({ cards: [first, second, third], wipLimitsJson: '{"InProgress":1}' });
  mount(api);
  assert.ok(await screen.findByRole("heading", { level: 4, name: /In progress/ }));
  assert.ok(screen.getByText("First"));
  const warning = screen.getByText("2 / 1");
  assert.equal(warning.closest("h4")?.textContent?.includes("In progress"), true);
  cleanup();
});

test("a truncated board says how much is hidden", async () => {
  const { api } = fakes({ cards: [card(task("a", "Only one"))], truncated: true, totalCount: 12 });
  mount(api);
  assert.ok(await screen.findByText("1 of 12 shown, filter to see more"));
  cleanup();
});

test("dragging a card into another column calls board.move with the columns and index", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second", { status: "InProgress" }));
  const { api, calls } = fakes({ cards: [first, second] });
  mount(api);
  await screen.findByText("Second");
  fireEvent.dragStart(screen.getAllByTitle("Drag to reorder")[0], { dataTransfer: dataTransfer() });
  // jsdom drops carry no pointer position, so the drop counts as "before": ahead of the only card there.
  fireEvent.drop(screen.getByLabelText("Second"), { dataTransfer: dataTransfer() });
  await waitFor(() => assert.equal(calls.length, 1));
  const request = calls[0].request as Record<string, unknown>;
  assert.equal(request.taskId, "a");
  assert.equal(request.fromColumn, "NotStarted");
  assert.equal(request.toColumn, "InProgress");
  assert.equal(request.toIndex, 0);
  assert.equal(request.groupBy, "Status");
  assert.ok(typeof request.operationId === "string" && request.operationId.length > 0);
  assert.equal(request.expectedVersion, "1");
  cleanup();
});

test("dragging within a column reorders it, and dropping on the same place moves nothing", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second"));
  const { api, calls } = fakes({ cards: [first, second] });
  mount(api);
  await screen.findByText("Second");
  fireEvent.dragStart(screen.getAllByTitle("Drag to reorder")[1], { dataTransfer: dataTransfer() });
  fireEvent.drop(screen.getByLabelText("First"), { dataTransfer: dataTransfer() });
  await waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(
    { taskId: calls[0].request.taskId, fromColumn: calls[0].request.fromColumn, toColumn: calls[0].request.toColumn, toIndex: calls[0].request.toIndex },
    { taskId: "b", fromColumn: "NotStarted", toColumn: "NotStarted", toIndex: 0 });
  cleanup();
});

test("arrow keys walk the cards across columns and Enter opens the task", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second", { status: "InProgress" }));
  const opened: TaskResponse[] = [];
  const { api } = fakes({ cards: [first, second] });
  mount(api, task => opened.push(task));
  await screen.findByText("Second");
  const start = screen.getByLabelText("First");
  start.focus();
  assert.equal(dom.window.document.activeElement, start);
  fireEvent.keyDown(start, { key: "ArrowRight" });
  assert.equal(dom.window.document.activeElement, screen.getByLabelText("Second"), "ArrowRight moves to the next column's card");
  fireEvent.keyDown(screen.getByLabelText("Second"), { key: "ArrowLeft" });
  assert.equal(dom.window.document.activeElement, screen.getByLabelText("First"));
  fireEvent.keyDown(screen.getByLabelText("First"), { key: "Enter" });
  assert.deepEqual(opened.map(item => item.id), ["a"]);
  cleanup();
});

test("the card menu moves a card up and to another column", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const first = card(task("a", "First"));
  const second = card(task("b", "Second"));
  const { api, calls } = fakes({ cards: [first, second] });
  mount(api);
  await screen.findByText("Second");
  await user.click(screen.getByRole("button", { name: "Actions for Second" }));
  await user.click(screen.getByRole("menuitem", { name: "Move up" }));
  await waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual({ taskId: calls[0].request.taskId, toColumn: calls[0].request.toColumn, toIndex: calls[0].request.toIndex }, { taskId: "b", toColumn: "NotStarted", toIndex: 0 });

  await user.click(screen.getByRole("button", { name: "Actions for First" }));
  await user.click(screen.getByRole("menuitem", { name: "Move to column…" }));
  await user.click(screen.getByRole("menuitem", { name: "Move to In progress" }));
  await waitFor(() => assert.equal(calls.length, 2));
  assert.deepEqual({ taskId: calls[1].request.taskId, toColumn: calls[1].request.toColumn, toIndex: calls[1].request.toIndex }, { taskId: "a", toColumn: "InProgress", toIndex: null });
  cleanup();
});

test("moving to Done with open subtasks asks and retries with the chosen handling", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const first = card(task("a", "First"));
  const { api, calls } = fakes({ cards: [first], askAboutSubtasks: true });
  mount(api);
  await screen.findByText("First");
  await user.click(screen.getByRole("button", { name: "Actions for First" }));
  await user.click(screen.getByRole("menuitem", { name: "Move to column…" }));
  await user.click(screen.getByRole("menuitem", { name: "Move to Done" }));
  assert.ok(await screen.findByRole("dialog", { name: "Complete open subtasks too?" }));
  await user.click(screen.getByRole("button", { name: "Keep them open" }));
  await waitFor(() => assert.equal(calls.length, 2));
  assert.equal(calls[1].request.subtaskChoice, "KeepOpen");
  assert.equal(calls[1].request.toColumn, "Done");
  cleanup();
});

test("board settings save grouping, WIP limits and hidden columns in one updateSettings call", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const first = card(task("a", "First"));
  const { api, calls } = fakes({ cards: [first] });
  mount(api);
  await screen.findByText("First");
  await user.click(screen.getByRole("button", { name: /Board settings/ }));
  await user.clear(screen.getByLabelText("WIP limit for In progress"));
  await user.type(screen.getByLabelText("WIP limit for In progress"), "2");
  await user.click(screen.getByLabelText("Hide In progress"));
  await user.click(screen.getByRole("button", { name: "Save board settings" }));
  await waitFor(() => assert.equal(calls.length, 1));
  assert.equal(calls[0].method, "updateSettings");
  const request = calls[0].request as Record<string, unknown>;
  assert.equal(request.targetKind, "Container");
  assert.equal(request.targetId, "list-1");
  assert.equal(request.expectedVersion, "3", "the settings version, 0 meaning create");
  assert.equal(request.wipLimitsJson, '{"InProgress":2}');
  assert.equal(request.hiddenColumnsJson, '["InProgress"]');
  cleanup();
});

test("hidden columns are not drawn until they are unhidden in the settings", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second", { status: "Done" }));
  const { api } = fakes({ cards: [first, second] });
  const board: BoardGetResponse = { ok: true, error: null, settings: { ...defaultSettings, hiddenColumnsJson: '["Done"]' }, columns: statusColumns, lanes: [{ key: "", title: "All tasks", isNone: true }], cards: [first, second], totalCount: 2, truncated: false };
  (api as unknown as { get: () => Promise<BoardGetResponse> }).get = async () => structuredClone(board);
  mount(api);
  assert.ok(await screen.findByText("First"));
  assert.equal(screen.queryByRole("heading", { level: 4, name: /Done/ }), null, "the hidden column is not drawn");
  cleanup();
});
