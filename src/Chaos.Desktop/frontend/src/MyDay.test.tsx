import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { myDayRpc, queryRpc, type DayClockResponse, type DaySummaryResponse, type QueryRowResponse, type TaskQueryRequest, type TaskResponse } from "#neoastra";

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
});
const { render, screen, waitFor, cleanup, fireEvent, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { MyDay, addDays, nextCheckDelay } = await import("./MyDay");

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return { id, title, spaceId: "space", containerId: "inbox", notes: "", status: "NotStarted", plannedDate: null, dueDate: null, version: "1", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null, ...patch };
}
const row = (item: TaskResponse): QueryRowResponse => ({ task: item, containerName: "Inbox", containerArchived: false, tagIds: [], matches: [], groupDate: null, waitingPerson: null, nextFollowUpAtUtc: null });
const clock = (today: string): DayClockResponse => ({ today, nextDayStartsAtUtc: `${addDays(today, 1)}T00:00:00.0000000Z`, zoneId: "UTC" });
const summary = (date: string, today: string, open = 0): DaySummaryResponse =>
  ({ date, clock: clock(today), exists: true, editable: date >= today, openCount: open, finishedCount: 0, remainingMinutes: 0, unknownEstimates: open });

/** A fake day service: one chosen task per date in `days`, suggestions for today, and a log of changes. */
function fakes(today: string, days: Record<string, TaskResponse[]>, suggested: TaskResponse[] = []) {
  const calls: { method: string; request: unknown }[] = [];
  const queries: TaskQueryRequest[] = [];
  const change = (method: string) => async (request: { date?: string; fromDate?: string }) => {
    calls.push({ method, request });
    return { ok: true, day: summary(request.date ?? request.fromDate ?? today, today), changed: 1, replayed: false, error: null };
  };
  const api = {
    ...myDayRpc,
    clock: async () => ({ ok: true, clock: clock(today), error: null }),
    open: async (request: { date: string | null }) => ({ ok: true, day: summary(request.date ?? today, today, (days[request.date ?? today] ?? []).length), error: null }),
    suggestions: async () => ({ ok: true, suggestions: suggested.map(item => ({ task: item, containerName: "Inbox", reason: "Overdue" })), error: null }),
    add: change("add"), remove: change("remove"), move: change("move"), carryOver: change("carryOver"),
  } as unknown as typeof myDayRpc;
  const query = {
    ...queryRpc,
    run: async (request: TaskQueryRequest) => {
      queries.push(request);
      const rows = request.statuses?.length ? [] : (days[request.day ?? today] ?? []).map(row);
      return { ok: true, rows, totalCount: rows.length, nextCursor: null, error: null };
    },
  } as unknown as typeof queryRpc;
  return { api, query, calls, queries };
}

test("calendar helpers add days across month ends and check again soon after midnight", () => {
  assert.equal(addDays("2026-02-28", 1), "2026-03-01");
  assert.equal(addDays("2026-01-01", -1), "2025-12-31");
  const now = Date.parse("2026-11-10T23:59:30Z");
  assert.equal(nextCheckDelay(clock("2026-11-10"), now), 31_000);
  assert.equal(nextCheckDelay(clock("2026-11-10"), Date.parse("2026-11-10T08:00:00Z")), 60_000);
});

test("suggestions are only offered; adding one sends today's date and nothing is added by itself", async () => {
  const late = task("t1", "Pay the invoice", { dueDate: "2026-11-08" });
  const { api, query, calls, queries } = fakes("2026-11-10", {}, [late]);
  const user = userEvent.setup({ document: dom.window.document });
  render(<MyDay clock={clock("2026-11-10")} revision={0} selectedTaskId={null} busy={false} api={api} queryApi={query} onOpen={() => {}} onChanged={() => {}}/>, { container: dom.window.document.body });
  await screen.findByText("Nothing chosen yet.");
  await screen.findByText("Pay the invoice");
  assert.equal(calls.length, 0);
  assert.ok(queries.every(request => request.view === "MyDay" && request.day === "2026-11-10"));
  await user.click(screen.getByRole("button", { name: "Add Pay the invoice to My Day" }));
  await waitFor(() => assert.deepEqual(calls.map(call => call.method), ["add"]));
  assert.deepEqual(calls[0].request, { operationId: (calls[0].request as { operationId: string }).operationId, date: "2026-11-10", taskId: "t1" });
  cleanup();
});

test("an earlier day is read-only but reviewable, and its unfinished work can be carried to today", async () => {
  const left = task("t2", "Draft the report");
  const { api, query, calls } = fakes("2026-11-10", { "2026-11-09": [left], "2026-11-10": [] });
  const user = userEvent.setup({ document: dom.window.document });
  render(<MyDay clock={clock("2026-11-10")} revision={0} selectedTaskId={null} busy={false} api={api} queryApi={query} onOpen={() => {}} onChanged={() => {}}/>, { container: dom.window.document.body });
  await screen.findByText("Nothing chosen yet.");
  await user.click(screen.getByRole("button", { name: "Previous day" }));
  await screen.findByRole("heading", { name: "Yesterday" });
  await screen.findByText("Draft the report");
  assert.equal(screen.queryByRole("button", { name: /Remove Draft the report/ }), null);
  await user.click(screen.getByRole("button", { name: "Carry unfinished to today" }));
  await waitFor(() => assert.deepEqual(calls.map(call => call.method), ["carryOver"]));
  assert.equal((calls[0].request as { fromDate: string }).fromDate, "2026-11-09");
  cleanup();
});

test("when the local date rolls over, a person looking at today moves to the new today", async () => {
  const { api, query, queries } = fakes("2026-11-11", { "2026-11-10": [task("t3", "Old day task")], "2026-11-11": [task("t4", "New day task")] });
  const props = { revision: 0, selectedTaskId: null, busy: false, api, queryApi: query, onOpen: () => {}, onChanged: () => {} };
  const view = render(<MyDay clock={clock("2026-11-10")} {...props}/>, { container: dom.window.document.body });
  await screen.findByText("Old day task");
  view.rerender(<MyDay clock={clock("2026-11-11")} {...props}/>);
  await screen.findByText("New day task");
  assert.equal(screen.queryByText("Old day task"), null);
  assert.ok(queries.some(request => request.day === "2026-11-11"));
  cleanup();
});

test("chosen tasks can be reordered with a drag handle or keyboard and removed from the day", async () => {
  const { api, query, calls } = fakes("2026-11-10", { "2026-11-10": [task("a", "First"), task("b", "Second")] });
  const user = userEvent.setup({ document: dom.window.document });
  render(<MyDay clock={clock("2026-11-10")} revision={0} selectedTaskId={null} busy={false} api={api} queryApi={query} onOpen={() => {}} onChanged={() => {}}/>, { container: dom.window.document.body });
  await screen.findByText("Second");
  assert.equal(screen.queryByRole("button", { name: /Move .* (up|down)/ }), null);
  fireEvent.keyDown(screen.getByRole("button", { name: "Reorder First" }), { key: "ArrowUp" });
  assert.equal(calls.length, 0);
  const dataTransfer = { effectAllowed: "", dropEffect: "", setData: () => {} };
  fireEvent.dragStart(screen.getByRole("button", { name: "Reorder Second" }), { dataTransfer });
  assert.equal(dataTransfer.effectAllowed, "move");
  const firstRow = screen.getByRole("button", { name: "First" }).closest("li")!.firstElementChild!;
  Object.defineProperty(firstRow, "getBoundingClientRect", { value: () => ({ top: 0, height: 40 }) });
  fireEvent.dragOver(firstRow, { dataTransfer, clientY: 1 });
  assert.equal(dataTransfer.dropEffect, "move");
  fireEvent.drop(firstRow, { dataTransfer, clientY: 1 });
  await waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual({ ...(calls[0].request as object), operationId: "" }, { operationId: "", date: "2026-11-10", taskId: "b", index: 0 });
  await waitFor(() => assert.equal((screen.getByRole("button", { name: "Reorder First" }) as HTMLButtonElement).disabled, false));
  fireEvent.keyDown(screen.getByRole("button", { name: "Reorder First" }), { key: "ArrowDown" });
  await waitFor(() => assert.equal(calls.length, 2));
  assert.deepEqual({ ...(calls[1].request as object), operationId: "" }, { operationId: "", date: "2026-11-10", taskId: "a", index: 1 });
  await waitFor(() => assert.equal((screen.getByRole("button", { name: "Remove First from this day" }) as HTMLButtonElement).disabled, false));
  await user.click(screen.getByRole("button", { name: "Remove First from this day" }));
  await waitFor(() => assert.deepEqual(calls.map(call => call.method), ["move", "move", "remove"]));
  cleanup();
});

test("chosen tasks offer completion and a status menu while earlier days remain read-only", async () => {
  const item = task("a", "Plan the meeting");
  const { api, query } = fakes("2026-11-10", { "2026-11-10": [item], "2026-11-09": [item] });
  const changes: string[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<MyDay clock={clock("2026-11-10")} revision={0} selectedTaskId={null} busy={false} api={api} queryApi={query} onOpen={() => {}} onChanged={() => {}} onSetStatus={(_, status) => changes.push(status)}/>, { container: dom.window.document.body });
  await user.click(await screen.findByRole("checkbox", { name: "Mark Plan the meeting as done" }));
  assert.deepEqual(changes, ["Done"]);
  fireEvent.contextMenu(screen.getByRole("button", { name: "Plan the meeting" }));
  const menu = await screen.findByRole("menu", { name: "Status for Plan the meeting" });
  assert.equal(within(menu).queryByRole("menuitem", { name: "Done" }), null);
  fireEvent.click(within(menu).getByRole("menuitem", { name: "In Progress" }));
  assert.deepEqual(changes, ["Done", "InProgress"]);
  await user.click(screen.getByRole("button", { name: "Previous day" }));
  await screen.findByRole("heading", { name: "Yesterday" });
  await screen.findByRole("button", { name: "Plan the meeting" });
  assert.equal(screen.queryByRole("checkbox", { name: "Mark Plan the meeting as done" }), null);
  cleanup();
});
