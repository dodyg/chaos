import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, queryRpc, tagRpc, taskRpc, type QueryRowResponse, type TagResponse, type TaskQueryRequest, type TaskResponse } from "#neoastra";

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
const { render, screen, waitFor, cleanup, within, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { App } = await import("./App");
const { TagPicker } = await import("./tags");

function task(id: string, title: string): TaskResponse {
  return { id, title, spaceId: "space", containerId: "inbox", notes: "", status: "NotStarted", plannedDate: null, dueDate: null, version: "1", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null };
}
function tag(id: string, path: string, parentId: string | null = null): TagResponse {
  return { id, name: path.split("/").at(-1)!, path, parentId, color: path === "home" ? "green" : null, version: "1", depth: path.split("/").length - 1 };
}
function resultRow(item: TaskResponse, tagIds: string[] = [], matches: string[] = ["Task"]): QueryRowResponse {
  return { task: item, containerName: "Inbox", containerArchived: false, tagIds, matches, groupDate: null };
}

const tags = [tag("home", "home"), tag("garden", "home/garden", "home"), tag("work", "work")];

/** A quiet API: empty inbox, known tags and counts; individual tests override what they observe. */
function api(overrides: { tagRpc?: Partial<typeof tagRpc>; queryRpc?: Partial<typeof queryRpc>; inbox?: TaskResponse[] } = {}) {
  return {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: overrides.inbox ?? [], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }) },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
    tagRpc: { ...tagRpc,
      list: async () => ({ ok: true, tags, error: null }),
      listDeleted: async () => ({ ok: true, tags: [], error: null }),
      targetTags: async () => ({ ok: true, tagIds: [], error: null }),
      suggest: async () => ({ ok: true, tags: [], error: null }),
      ...overrides.tagRpc },
    queryRpc: { ...queryRpc,
      counts: async () => ({ ok: true, views: [{ view: "Today", count: 3 }], tags: [{ tagId: "home", count: 2 }], error: null }),
      run: async () => ({ ok: true, rows: [], totalCount: 0, nextCursor: null, error: null }),
      ...overrides.queryRpc },
  };
}

test("the tag tree nests tags, shows counts and opens a tag view whose result opens the editor", async () => {
  const requests: TaskQueryRequest[] = [];
  const found = task("t1", "Weed the beds");
  const user = userEvent.setup({ document: dom.window.document });
  render(<App templates={<></>} api={api({
    queryRpc: { run: async (request: TaskQueryRequest) => { requests.push(request); return { ok: true, rows: [resultRow(found, ["garden"])], totalCount: 1, nextCursor: null, error: null }; } },
    tagRpc: { targetTags: async () => ({ ok: true, tagIds: ["garden"], error: null }) },
  })}/>, { container: dom.window.document.body });

  const tree = await screen.findByRole("navigation", { name: "Tags" });
  const home = within(tree).getByRole("button", { name: /home/ });
  await waitFor(() => assert.match(home.textContent ?? "", /2$/));
  assert.equal(within(tree).getByRole("button", { name: /garden/ }).style.paddingLeft, "1.35rem");
  assert.match(within(screen.getByRole("navigation", { name: "Views" })).getByRole("button", { name: /Today/ }).textContent ?? "", /3$/);

  await user.click(home);
  await screen.findByRole("heading", { name: "#home" });
  await waitFor(() => assert.deepEqual(requests.at(-1) && { view: requests.at(-1)!.view, tagId: requests.at(-1)!.tagId }, { view: "Tag", tagId: "home" }));
  const results = await screen.findByRole("list", { name: "Results" });
  assert.ok(within(results).getByText("home/garden"));
  await user.click(within(results).getByRole("button", { name: "Weed the beds" }));
  await user.click(screen.getByRole("tab", { name: "Details" })); // tags live on the editor's Details tab
  const applied = await screen.findByRole("list", { name: "Tags applied" });
  assert.ok(within(applied).getByText("home/garden"));
  cleanup();
});

test("search runs the typed words and pages with the server cursor", async () => {
  const requests: TaskQueryRequest[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<App templates={<></>} api={api({
    queryRpc: { run: async (request: TaskQueryRequest) => {
      requests.push(request);
      return request.cursor === null
        ? { ok: true, rows: [resultRow(task("a", "Call landlord"), [], ["Subtask"])], totalCount: 2, nextCursor: "page-2", error: null }
        : { ok: true, rows: [resultRow(task("b", "Landlord deposit"))], totalCount: 2, nextCursor: null, error: null };
    } },
  })}/>, { container: dom.window.document.body });

  await user.type(await screen.findByRole("searchbox", { name: "Search tasks" }), "landlord{Enter}");
  const results = await screen.findByRole("list", { name: "Results" });
  assert.ok(within(results).getByText("Matched in a subtask"));
  assert.deepEqual({ view: requests.at(-1)!.view, text: requests.at(-1)!.text }, { view: "Search", text: "landlord" });
  await user.click(screen.getByRole("button", { name: "Load more (1 more)" }));
  await within(results).findByRole("button", { name: "Landlord deposit" });
  assert.equal(requests.at(-1)!.cursor, "page-2");
  assert.equal(within(results).getAllByRole("button").length, 2);
  assert.equal(screen.queryByRole("button", { name: /Load more/ }), null);
  cleanup();
});

test("a temporary status filter is sent with the view and can be cleared", async () => {
  const requests: TaskQueryRequest[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<App templates={<></>} api={api({ queryRpc: { run: async (request: TaskQueryRequest) => { requests.push(request); return { ok: true, rows: [], totalCount: 0, nextCursor: null, error: null }; } } })}/>, { container: dom.window.document.body });

  await user.click(await screen.findByRole("button", { name: /Assigned to me/ }));
  const filters = await screen.findByRole("form", { name: "Filters" });
  await user.selectOptions(within(filters).getByRole("combobox", { name: "Status" }), "InProgress");
  await waitFor(() => assert.deepEqual(requests.at(-1)!.statuses, ["InProgress"]));
  assert.equal(requests.at(-1)!.view, "AssignedToMe");
  await user.click(within(filters).getByRole("button", { name: "Clear filters" }));
  await waitFor(() => assert.deepEqual(requests.at(-1)!.statuses, []));
  cleanup();
});

test("dropping a task on a tag tags it without moving it", async () => {
  const linked: Parameters<typeof tagRpc.link>[0][] = [];
  let moved = false;
  render(<App templates={<></>} api={{ ...api({
    inbox: [task("t1", "Buy seeds")],
    tagRpc: { link: async (request: Parameters<typeof tagRpc.link>[0]) => { linked.push(request); return { ok: true, tagIds: ["garden"], error: null }; } },
  }), containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }), moveTask: async () => { moved = true; return { ok: true, operationId: "", task: null, error: null, warning: null }; } } }}/>, { container: dom.window.document.body });

  const row = (await screen.findByRole("button", { name: "Buy seeds" })).closest("li")!;
  const garden = within(await screen.findByRole("navigation", { name: "Tags" })).getByRole("button", { name: /garden/ }).closest("li")!;
  const data = new Map<string, string>();
  const dataTransfer = { setData: (type: string, value: string) => { data.set(type, value); }, getData: (type: string) => data.get(type) ?? "", get types() { return [...data.keys()]; }, effectAllowed: "", dropEffect: "" };
  fireEvent.dragStart(within(row).getByTitle("Drag to reorder"), { dataTransfer });
  fireEvent.dragOver(garden, { dataTransfer });
  fireEvent.drop(garden, { dataTransfer });
  await waitFor(() => assert.equal(linked.length, 1));
  assert.deepEqual({ targetKind: linked[0].targetKind, targetId: linked[0].targetId, tagId: linked[0].tagId, path: linked[0].path }, { targetKind: "Task", targetId: "t1", tagId: "garden", path: null });
  assert.equal(moved, false);
  cleanup();
});

test("the tag picker suggests an existing near match and creates a nested path only when asked", async () => {
  const linked: Parameters<typeof tagRpc.link>[0][] = [];
  const errands = tag("errands", "errands");
  const pickerApi = { ...tagRpc,
    targetTags: async () => ({ ok: true, tagIds: [], error: null }),
    suggest: async (request: { text: string }) => ({ ok: true, tags: request.text.toLowerCase().startsWith("errand") ? [errands] : [], error: null }),
    link: async (request: Parameters<typeof tagRpc.link>[0]) => { linked.push(request); return { ok: true, tagIds: request.tagId ? [request.tagId] : ["new"], error: null }; },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<TagPicker targetKind="Task" targetId="t1" label="Tags" tags={[...tags, errands]} api={pickerApi} onChanged={() => {}}/>, { container: dom.window.document.body });

  const input = screen.getByRole("textbox", { name: "Tags" });
  await user.type(input, "errand");
  const suggestions = await screen.findByRole("list", { name: "Tag suggestions" });
  await within(suggestions).findByRole("button", { name: "Add #errands" });
  assert.ok(within(suggestions).getByRole("button", { name: "Create #errand" }));
  await user.click(within(suggestions).getByRole("button", { name: "Add #errands" }));
  await waitFor(() => assert.equal(linked.length, 1));
  assert.deepEqual({ tagId: linked[0].tagId, path: linked[0].path }, { tagId: "errands", path: null });

  await user.type(input, "home/shed{Enter}");
  await waitFor(() => assert.equal(linked.length, 2));
  assert.deepEqual({ tagId: linked[1].tagId, path: linked[1].path }, { tagId: null, path: "home/shed" });
  cleanup();
});
