import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { type TaskResponse } from "#neoastra";

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
const { render, screen, waitFor, cleanup, within, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { ListPage } = await import("./ListPage");

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id,
    title,
    spaceId: "space",
    containerId: "inbox",
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

function page(tasks: TaskResponse[]) {
  const edits: { taskId: string; category: string; priority: string }[] = [];
  let refreshed = 0;
  const taskApi = {
    edit: async (request: { taskId: string; category: string; priority: string; expectedVersion: string }) => {
      edits.push({ taskId: request.taskId, category: request.category, priority: request.priority });
      const found = tasks.find((item) => item.id === request.taskId)!;
      return {
        ok: true,
        task: { ...found, category: request.category, priority: request.priority, version: "2" },
        error: null,
        warning: null,
      };
    },
    setStatus: async () => ({ ok: false, task: null, error: { message: "no", fields: [] }, warning: null }),
  };
  const props = {
    tasks,
    deleted: [],
    sections: [],
    setSections: () => {},
    containers: [],
    containerId: "inbox",
    selected: undefined,
    busy: false,
    setBusy: () => {},
    setError: () => {},
    run: async () => {},
    runContainer: async () => {},
    refresh: async () => {
      refreshed++;
    },
    changed: () => {},
    refreshContainers: async () => {},
    onDeleted: async () => {},
    isStillSelected: () => true,
    editingId: null,
    setEditing: () => {},
    onOpen: () => {},
    onDelete: () => {},
    onSetStatus: () => {},
    onSetCategory: () => {},
    revision: 0,
    subtaskRevision: 0,
    focus: null,
    setFocus: () => {},
    showSettings: false,
    setShowSettings: () => {},
    captureApi: {},
    onCapture: async () => null,
    quickAddInput: React.createRef<HTMLTextAreaElement>(),
    containerApi: {},
    taskApi,
    tags: [],
  };
  return { props: props as unknown as React.ComponentProps<typeof ListPage>, edits, refreshed: () => refreshed };
}

test("select mode changes the category of every ticked task in one go", async () => {
  const items = [task("a", "Alpha"), task("b", "Bravo", { category: "Work" }), task("c", "Charlie")];
  const { props, edits, refreshed } = page(items);
  const user = userEvent.setup({ document: dom.window.document });
  render(<ListPage {...props} />, { container: dom.window.document.body });

  assert.equal(screen.queryByRole("toolbar", { name: "Bulk actions" }), null);
  assert.equal(screen.queryByRole("checkbox", { name: "Select Alpha" }), null);
  await user.click(screen.getByRole("button", { name: "Select" }));
  const bar = screen.getByRole("toolbar", { name: "Bulk actions" });
  assert.ok(within(bar).getByText("0 selected"));
  assert.equal((within(bar).getByRole("button", { name: "Make selected tasks Work" }) as HTMLButtonElement).disabled, true);

  await user.click(screen.getByRole("checkbox", { name: "Select Alpha" }));
  await user.click(screen.getByRole("checkbox", { name: "Select Bravo" }));
  assert.ok(within(bar).getByText("2 selected"));
  await user.click(within(bar).getByRole("button", { name: "Make selected tasks Work" }));
  // Bravo is already Work, so only Alpha is sent.
  await waitFor(() =>
    assert.deepEqual(
      edits.map((edit) => `${edit.taskId}:${edit.category}`),
      ["a:Work"],
    ),
  );
  await waitFor(() => assert.equal(refreshed(), 1));
  assert.ok(await within(bar).findByText("Made Work: 1 task updated."));
  cleanup();
});

test("select all, priority for everyone, and leaving select mode", async () => {
  const items = [task("a", "Alpha"), task("b", "Bravo")];
  const { props, edits } = page(items);
  const user = userEvent.setup({ document: dom.window.document });
  render(<ListPage {...props} />, { container: dom.window.document.body });
  await user.click(screen.getByRole("button", { name: "Select" }));
  await user.click(screen.getByRole("button", { name: "Select all 2" }));
  const bar = screen.getByRole("toolbar", { name: "Bulk actions" });
  assert.ok(within(bar).getByText("2 selected"));
  fireEvent.change(within(bar).getByLabelText("Set priority of selected tasks"), { target: { value: "High" } });
  await waitFor(() =>
    assert.deepEqual(
      edits.map((edit) => `${edit.taskId}:${edit.priority}`),
      ["a:High", "b:High"],
    ),
  );
  await user.click(within(bar).getByRole("button", { name: "Done selecting" }));
  assert.equal(screen.queryByRole("toolbar", { name: "Bulk actions" }), null);
  assert.equal(screen.queryByRole("checkbox", { name: "Select Alpha" }), null);
  cleanup();
});
