import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, recurrenceRpc, taskRpc, type ContainerResponse, type TaskResponse, type ListTasksResponse } from "#neoastra";

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
const { render, screen, waitFor, cleanup, act, within, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { App } = await import("./App");

function row(id: string, title: string): TaskResponse {
  return { id, title, spaceId: "space", containerId: id, notes: "", status: "NotStarted", plannedDate: null, dueDate: null, version: "1", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null };
}

test("deleting an active repeating task offers skip or end and delete", async () => {
  const repeated = { ...row("repeat", "Repeated"), containerId: "inbox" };
  let ended = false;
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [repeated], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }) },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
    recurrenceRpc: { ...recurrenceRpc,
      get: async () => ({ ok: true, rule: { mode: "FixedSchedule", frequency: "Day", interval: 1, anchorDate: "2026-11-10", anchorTime: null, zoneId: "UTC", weekdays: [], monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null, endKind: "Never", count: null, untilDate: null }, ended: false, occurrences: [{ taskId: repeated.id, nominalKey: "2026-11-10T00:00:00", effectiveLocal: "2026-11-10T00:00:00", state: "Active", completedAtUtc: null, revision: "1" }], error: null }),
      endAndDelete: async (request: { operationId: string }) => { ended = true; return { ok: true, task: repeated, operationId: request.operationId, error: null, warning: null }; },
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App api={api} templates={<></>}/>, { container: dom.window.document.body });
  await screen.findByRole("button", { name: "Repeated" });
  await user.click(screen.getByRole("button", { name: "Delete Repeated" }));
  const dialog = await screen.findByRole("dialog", { name: "Delete repeating task?" });
  assert.ok(within(dialog).getByRole("button", { name: "Skip this occurrence" }));
  await user.click(within(dialog).getByRole("button", { name: "End and delete" }));
  await waitFor(() => assert.equal(ended, true));
  cleanup();
});

test("a delayed previous list cannot replace the selected list", async () => {
  let finishA!: (value: ListTasksResponse) => void;
  const slowA = new Promise<ListTasksResponse>(resolve => { finishA = resolve; });
  let requestedA = false;
  let finishedA = false;
  const containers: ContainerResponse[] = ["a", "b"].map(id => ({ id, name: id.toUpperCase(), kind: "List", version: "1", archived: false, deadlineDate: null }));
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [], error: null }), listDeleted: async (request: { containerId: string | null }) => { if (request.containerId === "a") finishedA = true; return { ok: true, tasks: [], error: null }; } },
    containerRpc: { ...containerRpc,
      list: async () => ({ ok: true, containers, error: null }),
      listDeleted: async () => ({ ok: true, containers: [], error: null }),
      tasks: async (request: { containerId: string }) => { if (request.containerId === "a") { requestedA = true; return slowA; } return { ok: true, tasks: [row("b", "Task B")], error: null }; },
      sections: async () => ({ ok: true, sections: [], error: null }),
      deletedSections: async () => ({ ok: true, sections: [], error: null }),
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App api={api} templates={<></>} />, { container: dom.window.document.body });
  await screen.findByRole("button", { name: "A" });
  await user.click(within(screen.getByRole("navigation", { name: "Lists and projects" })).getByRole("button", { name: "A" }));
  await waitFor(() => assert.equal(requestedA, true));
  await user.click(within(screen.getByRole("navigation", { name: "Lists and projects" })).getByRole("button", { name: "B" }));
  await screen.findByRole("button", { name: "Task B" });
  await act(async () => { finishA({ ok: true, tasks: [row("a", "Task A")], error: null }); await slowA; });
  await waitFor(() => assert.equal(finishedA, true));
  assert.equal(screen.queryByRole("button", { name: "Task A" }), null);
  assert.ok(screen.getByRole("button", { name: "Task B" }));
  cleanup();
});

test("tasks are grouped under their section and ordered within that group", async () => {
  const container: ContainerResponse = { id: "project", name: "Project", kind: "List", version: "1", archived: false, deadlineDate: null };
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }) },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [container], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }),
      tasks: async () => ({ ok: true, tasks: [{ ...row("one", "Unsectioned"), sectionId: null }, { ...row("two", "Section task"), sectionId: "section" }], error: null }),
      sections: async () => ({ ok: true, sections: [{ id: "section", title: "Phase", version: "1" }], error: null }),
      deletedSections: async () => ({ ok: true, sections: [], error: null }),
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App api={api} templates={<></>} />, { container: dom.window.document.body });
  await screen.findByRole("button", { name: "Project" });
  await user.click(within(screen.getByRole("navigation", { name: "Lists and projects" })).getByRole("button", { name: "Project" }));
  const section = await screen.findByRole("list", { name: "Phase tasks" });
  await waitFor(() => assert.ok(within(section).getByRole("button", { name: "Section task" })));
  assert.ok(within(screen.getByRole("list", { name: "Unsectioned tasks" })).getByRole("button", { name: "Unsectioned" }));
  assert.equal((within(section).getByRole("button", { name: "Move Section task up" }) as HTMLButtonElement).disabled, true);
  cleanup();
});

test("dragging a task below a sibling sends the corresponding reorder", async () => {
  const first = { ...row("first", "First"), containerId: "inbox" };
  const second = { ...row("second", "Second"), containerId: "inbox" };
  let requested: { taskId: string; insertionIndex: number; expectedVersion: string } | null = null;
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [first, second], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }),
      reorder: async (request: { operationId: string; taskId: string; expectedVersion: string; insertionIndex: number }) => { requested = request; return { ok: true, operationId: request.operationId, task: first, error: null, warning: null }; },
    },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
  };
  render(<App api={api} templates={<></>} />, { container: dom.window.document.body });
  const firstButton = await screen.findByRole("button", { name: "First" });
  const secondButton = screen.getByRole("button", { name: "Second" });
  const source = firstButton.closest("li")!;
  const target = secondButton.closest("li")!;
  const dataTransfer = { setData() {}, effectAllowed: "", dropEffect: "" };
  fireEvent.dragStart(within(source).getByTitle("Drag to reorder"), { dataTransfer });
  fireEvent.dragOver(target, { dataTransfer, clientY: 100 });
  const drop = new dom.window.MouseEvent("drop", { bubbles: true, cancelable: true, clientY: 100 });
  Object.defineProperty(drop, "dataTransfer", { value: dataTransfer });
  fireEvent(target, drop);
  await waitFor(() => assert.deepEqual(requested && { taskId: requested.taskId, insertionIndex: requested.insertionIndex, expectedVersion: requested.expectedVersion }, { taskId: "first", insertionIndex: 1, expectedVersion: "1" }));
  cleanup();
});

test("dragging onto a different section does not reorder the wrong sibling set", async () => {
  const container: ContainerResponse = { id: "project", name: "Project", kind: "List", version: "1", archived: false, deadlineDate: null };
  let reordered = false;
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }),
      reorder: async (request: { operationId: string }) => { reordered = true; return { ok: true, operationId: request.operationId, task: row("one", "Outside"), error: null, warning: null }; },
    },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [container], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }),
      tasks: async () => ({ ok: true, tasks: [{ ...row("one", "Outside"), containerId: "project" }, { ...row("two", "Inside"), containerId: "project", sectionId: "section" }], error: null }),
      sections: async () => ({ ok: true, sections: [{ id: "section", title: "Phase", version: "1" }], error: null }),
      deletedSections: async () => ({ ok: true, sections: [], error: null }),
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App api={api} templates={<></>} />, { container: dom.window.document.body });
  await screen.findByRole("button", { name: "Project" });
  await user.click(within(screen.getByRole("navigation", { name: "Lists and projects" })).getByRole("button", { name: "Project" }));
  const outside = (await screen.findByRole("button", { name: "Outside" })).closest("li")!;
  const inside = (await screen.findByRole("button", { name: "Inside" })).closest("li")!;
  const dataTransfer = { setData() {}, effectAllowed: "", dropEffect: "" };
  fireEvent.dragStart(within(outside).getByTitle("Drag to reorder"), { dataTransfer });
  fireEvent.drop(inside, { dataTransfer });
  assert.equal(reordered, false);
  cleanup();
});

test("dragging a list in the list navigation reorders it and keeps the Inbox first", async () => {
  const containers: ContainerResponse[] = [
    { id: "inbox", name: "Inbox", kind: "Inbox", version: "1", archived: false, deadlineDate: null },
    { id: "a", name: "Alpha", kind: "List", version: "2", archived: false, deadlineDate: null },
    { id: "b", name: "Beta", kind: "Project", version: "5", archived: false, deadlineDate: null },
  ];
  const calls: Parameters<typeof containerRpc.reorder>[0][] = [];
  let listCalls = 0;
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }) },
    containerRpc: { ...containerRpc,
      list: async () => { listCalls++; return { ok: true, containers, error: null }; },
      listDeleted: async () => ({ ok: true, containers: [], error: null }),
      reorder: async (request: Parameters<typeof containerRpc.reorder>[0]) => { calls.push(request); return { ok: true, container: containers[2], error: null }; },
    },
  };
  render(<App api={api} templates={<></>} />, { container: dom.window.document.body });
  const nav = await screen.findByRole("navigation", { name: "Lists and projects" });
  const alpha = within(nav).getByRole("button", { name: "Alpha" }).closest("li")!;
  const beta = within(nav).getByRole("button", { name: "Beta" }).closest("li")!;
  assert.equal(within(nav).queryByRole("button", { name: "Inbox" }), null);
  const dataTransfer = { setData() {}, effectAllowed: "", dropEffect: "" };
  fireEvent.dragStart(within(beta).getByTitle("Drag to reorder"), { dataTransfer });
  fireEvent.dragOver(alpha, { dataTransfer, clientY: 0 });
  fireEvent.drop(alpha, { dataTransfer, clientY: 0 });
  await waitFor(() => assert.equal(listCalls, 2));
  assert.deepEqual(calls.map(r => ({ containerId: r.containerId, expectedVersion: r.expectedVersion, insertionIndex: r.insertionIndex })), [{ containerId: "b", expectedVersion: "5", insertionIndex: 1 }]);
  cleanup();
});
