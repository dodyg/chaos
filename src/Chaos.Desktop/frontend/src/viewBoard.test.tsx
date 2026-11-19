import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import {
  type BoardCardResponse,
  type BoardColumnResponse,
  type BoardGetResponse,
  type BoardSettingsDto,
  type TaskResponse,
} from "#neoastra";

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
const { render, screen, waitFor, cleanup, fireEvent, createEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { Board } = await import("./Board");

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id,
    title,
    spaceId: "space",
    containerId: "list-1",
    notes: "",
    status: "NotStarted",
    plannedDate: null,
    dueDate: null,
    version: "1",
    createdAtUtc: "2026-01-01T00:00:00Z",
    updatedAtUtc: "2026-01-01T00:00:00Z",
    priority: "Normal",
    estimateMinutes: null,
    category: "Personal",
    plannedTime: null,
    plannedZoneId: null,
    dueTime: null,
    dueZoneId: null,
    sectionId: null,
    ...patch,
  };
}
const statusColumns: BoardColumnResponse[] = [
  { key: "NotStarted", title: "Not started", isNone: false },
  { key: "InProgress", title: "In progress", isNone: false },
  { key: "Done", title: "Done", isNone: false },
];
const settings: BoardSettingsDto = {
  groupBy: "Status",
  swimlaneBy: "None",
  columnTagId: null,
  swimlaneTagId: null,
  wipLimitsJson: "{}",
  hiddenColumnsJson: "[]",
  version: "2",
  isDefault: false,
};
const card = (item: TaskResponse): BoardCardResponse => ({
  task: item,
  tagIds: [],
  subtasksTotal: 0,
  subtasksDone: 0,
  hasIssueLink: false,
  columnKey: item.status,
  laneKey: "",
});

/** A fake board service: it serves a view's board and logs what the board asks of it. */
function fakes(cards: BoardCardResponse[]) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const board: BoardGetResponse = {
    ok: true,
    error: null,
    settings,
    columns: statusColumns,
    lanes: [{ key: "", title: "All tasks", isNone: true }],
    cards,
    totalCount: cards.length,
    truncated: false,
  };
  const api = {
    get: async (request: Record<string, unknown>) => {
      calls.push({ method: "get", request });
      return structuredClone(board);
    },
    getView: async (request: Record<string, unknown>) => {
      calls.push({ method: "getView", request });
      return structuredClone(board);
    },
    move: async (request: Record<string, unknown>) => {
      calls.push({ method: "move", request });
      return { ok: true, task: cards[0].task, error: null, warning: null };
    },
    updateSettings: async (request: Record<string, unknown>) => {
      calls.push({ method: "updateSettings", request });
      return { ok: true, settings: { ...settings, version: "3" }, error: null };
    },
  } as unknown as Parameters<typeof Board>[0]["api"];
  return { api, calls };
}

const dataTransfer = () => ({ effectAllowed: "", dropEffect: "", types: [], setData: () => {}, getData: () => "" });

afterEach(() => cleanup());

function mount(api: Parameters<typeof Board>[0]["api"]) {
  return render(
    <Board viewId="view-1" tags={[]} focus={"Work"} busy={false} revision={0} onOpen={() => {}} onChanged={() => {}} api={api} />,
    { container: dom.window.document.body },
  );
}

test("a view's board is read through getView with the view id and the focus, never through get", async () => {
  const { api, calls } = fakes([card(task("a", "First")), card(task("b", "Second", { status: "InProgress" }))]);
  mount(api);
  await screen.findByText("Second");
  assert.deepEqual(
    calls.map((call) => call.method),
    ["getView"],
  );
  assert.equal(calls[0].request.viewId, "view-1");
  assert.equal(calls[0].request.category, "Work");
});

test("a card menu in a view moves between columns without a position and offers no Move up or down", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = fakes([card(task("a", "First")), card(task("b", "Second"))]);
  mount(api);
  await screen.findByText("Second");
  const button = screen.getByRole("button", { name: "Actions for Second" }) as HTMLButtonElement;
  await waitFor(() => assert.equal(button.disabled, false));
  await user.click(button);
  assert.equal(screen.queryByRole("menuitem", { name: "Move up" }), null);
  assert.equal(screen.queryByRole("menuitem", { name: "Move down" }), null);
  await user.click(screen.getByRole("menuitem", { name: "Move to column…" }));
  await user.click(screen.getByRole("menuitem", { name: "Move to Done" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "move").length, 1));
  const move = calls.find((call) => call.method === "move")!.request;
  assert.equal(move.taskId, "b");
  assert.equal(move.toColumn, "Done");
  assert.equal(move.beforeTaskId, null);
  assert.equal(move.afterTaskId, null);
});

test("dropping a card above another in a view still sends no position, only the column", async () => {
  const { api, calls } = fakes([card(task("a", "First")), card(task("b", "Second"))]);
  mount(api);
  await screen.findByText("Second");
  const button = screen.getByRole("button", { name: "Actions for Second" }) as HTMLButtonElement;
  await waitFor(() => assert.equal(button.disabled, false));
  fireEvent.dragStart(screen.getByLabelText("Second"), { dataTransfer: dataTransfer() });
  const target = (screen.getByLabelText("First").closest("[data-board-cell]") as HTMLElement | null) ?? screen.getByLabelText("First");
  for (const type of ["dragOver", "drop"] as const) {
    const event = createEvent[type](target, { dataTransfer: dataTransfer() });
    Object.defineProperty(event, "clientY", { value: 0 });
    fireEvent(target, event);
  }
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "move").length, 1));
  const move = calls.find((call) => call.method === "move")!.request;
  assert.equal(move.beforeTaskId, null);
  assert.equal(move.afterTaskId, null);
});

test("the settings of a view's board save against the view and offer lanes by list but not sections", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = fakes([card(task("a", "First"))]);
  mount(api);
  await screen.findByText("First");
  await user.click(screen.getByRole("button", { name: /Board settings/ }));
  const groupBy = screen.getByRole("combobox", { name: "Group columns by" });
  assert.equal(
    Array.from((groupBy as HTMLSelectElement).options).some((option) => option.value === "Section"),
    false,
  );
  const lanes = screen.getByRole("combobox", { name: "Swimlanes" }) as HTMLSelectElement;
  assert.equal(
    Array.from(lanes.options).some((option) => option.value === "Container"),
    true,
  );
  await user.selectOptions(lanes, "Container");
  await user.click(screen.getByRole("button", { name: "Save board settings" }));
  await waitFor(() => assert.ok(calls.some((call) => call.method === "updateSettings")));
  const request = calls.find((call) => call.method === "updateSettings")!.request;
  assert.equal(request.targetKind, "View");
  assert.equal(request.targetId, "view-1");
  assert.equal(request.swimlaneBy, "Container");
  assert.equal(request.expectedVersion, "2");
});
