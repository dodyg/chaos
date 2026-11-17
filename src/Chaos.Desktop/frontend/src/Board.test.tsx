import { afterEach, test } from "node:test";
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
const { render, screen, waitFor, cleanup, fireEvent, createEvent, act } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { Board, dropPlacement, loadLayout, saveLayout } = await import("./Board");

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
function fakes(options: { cards: BoardCardResponse[]; wipLimitsJson?: string; truncated?: boolean; totalCount?: number; askAboutSubtasks?: boolean; lanes?: BoardGetResponse["lanes"]; failMoves?: boolean }) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const board: BoardGetResponse = {
    ok: true, error: null, settings: { ...defaultSettings, wipLimitsJson: options.wipLimitsJson ?? "{}" },
    columns: statusColumns, lanes: options.lanes ?? [{ key: "", title: "All tasks", isNone: true }],
    cards: options.cards, totalCount: options.totalCount ?? options.cards.length, truncated: options.truncated ?? false,
  };
  const api = {
    get: async () => structuredClone(board),
    move: async (request: Record<string, unknown> & { toColumn: string | null; subtaskChoice: string | null }) => {
      calls.push({ method: "move", request });
      if (options.failMoves) {
        return { ok: false, task: null, warning: null, error: { kind: "VersionConflict", message: "This task changed elsewhere while you were working. Refresh and try again.", fields: [], correlationId: "x" } };
      }
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

const dataTransfer = () => ({ effectAllowed: "", dropEffect: "", types: [], setData: () => {}, getData: () => "" });

/** Starts dragging a card: the whole card is the native drag source. */
function dragCard(title: string) {
  fireEvent.dragStart(screen.getByLabelText(title), { dataTransfer: dataTransfer() });
}

/**
 * Drops the dragged card on `element` at the pointer height `clientY`. jsdom lays nothing out
 * (every card box is 0 high at 0), so 0 counts as above every card and 1 as below them all.
 */
function dropAt(element: HTMLElement, clientY: number) {
  for (const type of ["dragOver", "drop"] as const) {
    const event = createEvent[type](element, { dataTransfer: dataTransfer() });
    Object.defineProperty(event, "clientY", { value: clientY });
    fireEvent(element, event);
  }
}

const placementOf = (request: Record<string, unknown>) => ({ beforeTaskId: request.beforeTaskId, afterTaskId: request.afterTaskId });

afterEach(() => cleanup());

/**
 * The focused element's accessible label. Compare labels, not elements: a failed assertion on
 * two jsdom elements makes node:assert inspect the whole DOM, which stalls a retrying waitFor.
 */
const focusedLabel = () => dom.window.document.activeElement?.getAttribute("aria-label") ?? null;

/** A card's actions button once the board is usable again (a write reads the board again first). */
async function actionsOf(title: string) {
  const button = screen.getByRole("button", { name: `Actions for ${title}` }) as HTMLButtonElement;
  await waitFor(() => assert.equal(button.disabled, false));
  return button;
}

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
});

test("a truncated board says how much is hidden", async () => {
  const { api } = fakes({ cards: [card(task("a", "Only one"))], truncated: true, totalCount: 12 });
  mount(api);
  assert.ok(await screen.findByText("1 of 12 shown, filter to see more"));
});

test("a drop is placed next to the card seen beside it, or nowhere when the card is already there", () => {
  const cell = ["a", "b", "c"];
  assert.deepEqual(dropPlacement(cell, "c", 0), { beforeTaskId: "a", afterTaskId: null }, "to the top");
  assert.deepEqual(dropPlacement(cell, "a", 2), { beforeTaskId: null, afterTaskId: "c" }, "to the bottom");
  assert.deepEqual(dropPlacement(cell, "a", 1), { beforeTaskId: "c", afterTaskId: null }, "between the others");
  assert.equal(dropPlacement(cell, "b", 1), null, "its own place");
  assert.deepEqual(dropPlacement(cell, "x", 3), { beforeTaskId: null, afterTaskId: "c" }, "from another column, at the end");
  assert.deepEqual(dropPlacement([], "x", 0), { beforeTaskId: null, afterTaskId: null }, "into an empty cell");
});

test("dragging a card into another column calls board.move with the columns and its new neighbour", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second", { status: "InProgress" }));
  const { api, calls } = fakes({ cards: [first, second] });
  mount(api);
  await screen.findByText("Second");
  dragCard("First");
  dropAt(screen.getByLabelText("Second"), 0);
  await waitFor(() => assert.equal(calls.length, 1));
  const request = calls[0].request as Record<string, unknown>;
  assert.equal(request.taskId, "a");
  assert.equal(request.fromColumn, "NotStarted");
  assert.equal(request.toColumn, "InProgress");
  assert.deepEqual(placementOf(request), { beforeTaskId: "b", afterTaskId: null });
  assert.equal(request.groupBy, "Status");
  assert.ok(typeof request.operationId === "string" && request.operationId.length > 0);
  assert.equal(request.expectedVersion, "1");
});

test("after a move the board reads itself again, so the next move sends the new version", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  let version = 1;
  let reads = 0;
  const moves: Record<string, unknown>[] = [];
  const read = (): BoardGetResponse => ({
    ok: true, error: null, settings: defaultSettings, columns: statusColumns, lanes: [{ key: "", title: "All tasks", isNone: true }],
    cards: [card(task("a", "First", { version: String(version) })), card(task("b", "Second", { version: String(version) }))], totalCount: 2, truncated: false,
  });
  const api = {
    get: async () => { reads++; return read(); },
    move: async (request: Record<string, unknown>) => {
      moves.push(request);
      if (request.expectedVersion !== String(version)) return { ok: false, task: null, warning: null, error: { kind: "VersionConflict", message: "This task changed elsewhere while you were working. Refresh and try again.", fields: [], correlationId: "x" } };
      version++; // the reorder bumps both cards
      return { ok: true, task: null, error: null, warning: null };
    },
    updateSettings: async () => ({ ok: true, settings: defaultSettings, error: null }),
  } as unknown as Parameters<typeof Board>[0]["api"];
  mount(api); // onChanged here does not change `revision`: the board reads itself again
  await screen.findByText("First");
  const menuOf = (title: string) => screen.getByRole("button", { name: `Actions for ${title}` });
  await user.click(menuOf("First"));
  await user.click(screen.getByRole("menuitem", { name: "Move down" }));
  await waitFor(() => assert.equal(moves.length, 1));
  await waitFor(() => assert.equal(reads, 2, "the board was read again after the move"), { timeout: 3000 });
  await waitFor(() => assert.equal((menuOf("Second") as HTMLButtonElement).disabled, false, "the board is usable once the read lands"));
  await user.click(menuOf("Second"));
  await user.click(screen.getByRole("menuitem", { name: "Move up" }));
  await waitFor(() => assert.equal(moves.length, 2));
  assert.equal(moves[1].expectedVersion, "2");
  assert.equal(screen.queryByText(/changed elsewhere/), null);
});

test("dropping below the last card of another column places it after that card", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second", { status: "InProgress" }));
  const { api, calls } = fakes({ cards: [first, second] });
  mount(api);
  await screen.findByText("Second");
  dragCard("First");
  dropAt(screen.getByRole("region", { name: /In progress column/ }), 1);
  await waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(placementOf(calls[0].request), { beforeTaskId: null, afterTaskId: "b" });
});

test("dragging within a column reorders it, and dropping on the same place moves nothing", async () => {
  const first = card(task("a", "First"));
  const second = card(task("b", "Second"));
  const { api, calls } = fakes({ cards: [first, second] });
  mount(api);
  await screen.findByText("Second");
  dragCard("First");
  dropAt(screen.getByLabelText("First"), 0);
  dragCard("Second");
  dropAt(screen.getByLabelText("First"), 0);
  await waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(
    { taskId: calls[0].request.taskId, fromColumn: calls[0].request.fromColumn, toColumn: calls[0].request.toColumn, ...placementOf(calls[0].request) },
    { taskId: "b", fromColumn: "NotStarted", toColumn: "NotStarted", beforeTaskId: "a", afterTaskId: null });
});

test("an empty cell shows where the dragged card will land, and ending the drag clears it", async () => {
  const { api } = fakes({ cards: [card(task("a", "First"))] });
  mount(api);
  await screen.findByText("First");
  dragCard("First");
  const done = screen.getByRole("region", { name: /Done column/ });
  await new Promise(resolve => setTimeout(resolve, 0)); // the drag styles the card after dragstart
  fireEvent.dragOver(done, { dataTransfer: dataTransfer() });
  assert.ok(await screen.findByText("Drop here."));
  fireEvent.dragEnd(screen.getByLabelText("First"), { dataTransfer: dataTransfer() });
  await waitFor(() => assert.equal(screen.queryByText("Drop here."), null));
});

test("a card keeps its swimlane: another lane refuses the drop", async () => {
  const high = card(task("a", "Urgent one", { priority: "High" }), { laneKey: "High" });
  const low = card(task("b", "Later one", { priority: "Low", status: "InProgress" }), { laneKey: "Low" });
  const { api, calls } = fakes({ cards: [high, low], lanes: [{ key: "High", title: "High", isNone: false }, { key: "Low", title: "Low", isNone: false }] });
  mount(api);
  await screen.findByText("Later one");
  dragCard("Urgent one");
  const otherLane = screen.getByRole("region", { name: "Low · In progress column" });
  const over = createEvent.dragOver(otherLane, { dataTransfer: dataTransfer() });
  fireEvent(otherLane, over);
  assert.equal(over.defaultPrevented, false, "the other lane does not accept the card");
  dropAt(otherLane, 0);
  dragCard("Urgent one");
  dropAt(screen.getByRole("region", { name: "High · In progress column" }), 0);
  await waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual({ toColumn: calls[0].request.toColumn, ...placementOf(calls[0].request) }, { toColumn: "InProgress", beforeTaskId: null, afterTaskId: null });
});

test("a refused move keeps the board on screen and says why", async () => {
  const first = card(task("a", "First"));
  const { api } = fakes({ cards: [first], failMoves: true });
  mount(api);
  await screen.findByText("First");
  dragCard("First");
  dropAt(screen.getByRole("region", { name: /In progress column/ }), 0);
  assert.ok(await screen.findByText(/changed elsewhere/));
  assert.ok(screen.getByText("First"), "the board stays drawn");
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
  assert.deepEqual({ taskId: calls[0].request.taskId, toColumn: calls[0].request.toColumn, ...placementOf(calls[0].request) }, { taskId: "b", toColumn: "NotStarted", beforeTaskId: "a", afterTaskId: null });

  await user.click(await actionsOf("First"));
  await user.click(screen.getByRole("menuitem", { name: "Move to column…" }));
  await user.click(screen.getByRole("menuitem", { name: "Move to In progress" }));
  await waitFor(() => assert.equal(calls.length, 2));
  assert.deepEqual({ taskId: calls[1].request.taskId, toColumn: calls[1].request.toColumn, ...placementOf(calls[1].request) }, { taskId: "a", toColumn: "InProgress", beforeTaskId: null, afterTaskId: null });
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
});

test("after a menu move the moved card has the focus, and Escape in a menu returns to its button", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api } = fakes({ cards: [card(task("a", "First")), card(task("b", "Second"))] });
  mount(api);
  await screen.findByText("Second");
  await user.click(await actionsOf("Second"));
  fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });
  assert.equal(screen.queryByRole("menu"), null);
  assert.equal(focusedLabel(), "Actions for Second");

  await user.click(await actionsOf("Second"));
  await user.click(screen.getByRole("menuitem", { name: "Move up" }));
  await waitFor(() => assert.equal(focusedLabel(), "Second"));
});

test("one card is always in the Tab order, even when the last focused card has left the board", async () => {
  let cards = [card(task("a", "First")), card(task("b", "Second"))];
  const api = {
    get: async () => ({ ok: true, error: null, settings: defaultSettings, columns: statusColumns, lanes: [{ key: "", title: "All tasks", isNone: true }], cards, totalCount: cards.length, truncated: false }),
    move: async () => ({ ok: true, task: null, error: null, warning: null }),
    updateSettings: async () => ({ ok: true, settings: defaultSettings, error: null }),
  } as unknown as Parameters<typeof Board>[0]["api"];
  const view = render(<Board containerId="list-1" tags={[]} focus={null} busy={false} revision={0} onOpen={() => {}} onChanged={() => {}} api={api}/>, { container: dom.window.document.body });
  await screen.findByText("Second");
  act(() => screen.getByLabelText("Second").focus());
  assert.equal(screen.getByLabelText("Second").tabIndex, 0);
  assert.equal(screen.getByLabelText("First").tabIndex, -1);
  cards = [cards[0]];
  view.rerender(<Board containerId="list-1" tags={[]} focus={null} busy={false} revision={1} onOpen={() => {}} onChanged={() => {}} api={api}/>);
  await waitFor(() => assert.equal(screen.queryByLabelText("Second"), null));
  assert.equal(screen.getByLabelText("First").tabIndex, 0, "the first card takes the Tab stop");
});
