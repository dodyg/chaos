import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import {
  boardRpc,
  containerRpc,
  queryRpc,
  tagRpc,
  taskRpc,
  spaceRpc,
  viewRpc,
  type QueryRowResponse,
  type TagResponse,
  type TaskResponse,
  type ViewDto,
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
const { render, screen, waitFor, cleanup, within, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { App } = await import("./App");
const { ViewEditor } = await import("./ViewEditor");
const { ViewTable } = await import("./ViewTable");
const model = await import("./viewModel");

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
function resultRow(item: TaskResponse): QueryRowResponse {
  return {
    task: item,
    containerName: "Inbox",
    containerArchived: false,
    tagIds: [],
    matches: ["Task"],
    groupDate: null,
    waitingPerson: null,
    nextFollowUpAtUtc: null,
    lastActivityAtUtc: null,
    delegatedTo: null,
  };
}
const tags: TagResponse[] = [{ id: "bug", name: "bug", path: "bug", parentId: null, color: null, version: "1", depth: 0 }];
const icons = ["list", "bug", "flag"];

// ---- The model ----------------------------------------------------------------------------------

const spec = {
  v: 1,
  match: "all",
  items: [
    { field: "status", op: "notIn", value: ["Done", "Cancelled"] },
    {
      match: "any",
      items: [
        { field: "tag", op: "hasAnyOf", value: ["bug"], descendants: true },
        { field: "priority", op: "atLeast", value: "High" },
      ],
    },
    { field: "due", op: "inRange", value: { relative: "next", days: 7 } },
  ],
};

test("a definition survives the editor model unchanged, nested groups and relative dates included", () => {
  const json = model.toDefinitionJson(model.fromDefinitionJson(JSON.stringify(spec)));
  assert.deepEqual(JSON.parse(json), spec);
  assert.equal(model.countConditions(model.fromDefinitionJson(json)), 4);
  assert.equal(model.depthOf(model.fromDefinitionJson(json)), 2);
});

test("a new condition for every field starts with an operator and value the host accepts", () => {
  for (const field of model.viewFields) {
    const condition = model.newCondition(field.name);
    assert.ok(
      field.operators.some((operator) => operator.op === condition.op),
      field.name,
    );
    const node = JSON.parse(model.toDefinitionJson(model.newGroup("all", [condition]))).items[0];
    assert.equal(node.field, field.name);
    assert.equal(node.op, condition.op);
  }
});

test("conditions with nothing chosen are incomplete, so the preview does not ask the host about them", () => {
  assert.equal(model.isComplete(model.newGroup("all", [model.newCondition("tag")])), false);
  assert.equal(model.isComplete(model.newGroup("all", [model.newCondition("text")])), false);
  assert.equal(model.isComplete(model.newGroup("all", [model.newCondition("status"), model.newCondition("due")])), true);
});

test("a layout survives the model and an unreadable one falls back to the default list", () => {
  const layout = { kind: "table" as const, group: "none", sortField: "priority", sortDescending: true, columns: ["title", "due"] };
  assert.deepEqual(model.layoutFromJson(model.layoutToJson(layout)), layout);
  assert.equal(model.layoutFromJson("not json").kind, "list");
});

// ---- The editor ---------------------------------------------------------------------------------

type SaveRequest = Parameters<typeof viewRpc.save>[0];

function editorApi(options: { previewCount?: number; fail?: boolean } = {}) {
  const saves: SaveRequest[] = [];
  const previews: Parameters<typeof viewRpc.preview>[0][] = [];
  const api = {
    preview: async (request: Parameters<typeof viewRpc.preview>[0]) => {
      previews.push(request);
      return {
        ok: true,
        rows: [resultRow(task("a", "First match"))],
        totalCount: options.previewCount ?? 7,
        nextCursor: null,
        layoutJson: null,
        error: null,
      };
    },
    save: async (request: SaveRequest) => {
      saves.push(request);
      if (options.fail)
        return {
          ok: false,
          view: null,
          error: { kind: "Validation", message: "Give the view a name of 1 to 80 characters.", fields: [], correlationId: "x" },
        };
      const view: ViewDto = {
        id: "v1",
        name: request.name,
        icon: request.icon ?? "list",
        color: request.color,
        pinned: request.pinned,
        position: 0,
        definitionJson: request.definitionJson,
        layoutJson: request.layoutJson ?? "",
        alertsEnabled: request.alertsEnabled,
        spaceId: null,
        version: "1",
        deleted: false,
      };
      return { ok: true, view, error: null };
    },
  };
  return { api, saves, previews };
}

test("the editor builds nested conditions, shows the live count and saves the exact definition", async () => {
  const { api, saves, previews } = editorApi({ previewCount: 7 });
  const user = userEvent.setup({ document: dom.window.document });
  const saved: ViewDto[] = [];
  render(
    <ViewEditor
      view={null}
      icons={icons}
      tags={tags}
      containers={[]}
      api={api}
      onSaved={(view) => saved.push(view)}
      onCancel={() => undefined}
    />,
    { container: dom.window.document.body },
  );
  try {
    await user.type(screen.getByRole("textbox", { name: "Name" }), "Open bugs");
    await user.click(screen.getByRole("button", { name: "Add condition" }));
    // The first row is a status condition; switch it to priority at least High.
    const row = screen.getByRole("group", { name: "Condition on Status" });
    await user.selectOptions(within(row).getByRole("combobox", { name: "Field" }), "priority");
    const priorityRow = await screen.findByRole("group", { name: "Condition on Priority" });
    await user.selectOptions(within(priorityRow).getByRole("combobox", { name: "Condition" }), "atLeast");
    // A nested "any" group holds a second condition.
    await user.click(screen.getByRole("button", { name: "Add group" }));
    assert.ok(screen.getByRole("group", { name: "Group, level 2" }));

    await waitFor(() => assert.match(screen.getByRole("status", { name: "Preview" }).textContent ?? "", /7 tasks match/));
    assert.ok(previews.length > 0);

    await user.click(screen.getByRole("button", { name: "Save view" }));
    await waitFor(() => assert.equal(saves.length, 1));
    const definition = JSON.parse(saves[0].definitionJson);
    assert.equal(definition.v, 1);
    assert.deepEqual(definition.items[0], { field: "priority", op: "atLeast", value: "High" });
    assert.equal(definition.items[1].match, "any");
    assert.equal(definition.items[1].items[0].field, "status");
    assert.equal(saves[0].name, "Open bugs");
    assert.equal(saves[0].viewId, null);
    assert.equal(saved.length, 1);
  } finally {
    cleanup();
  }
});

test("the editor shows the host's refusal and keeps the text, and a retry reuses the same operation", async () => {
  const { api, saves } = editorApi({ fail: true });
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewEditor view={null} icons={icons} tags={tags} containers={[]} api={api} onSaved={() => undefined} onCancel={() => undefined} />,
    { container: dom.window.document.body },
  );
  try {
    await user.type(screen.getByRole("textbox", { name: "Name" }), "Keep me");
    await user.click(screen.getByRole("button", { name: "Save view" }));
    await screen.findByText("Give the view a name of 1 to 80 characters.");
    await user.click(screen.getByRole("button", { name: "Save view" }));
    await waitFor(() => assert.equal(saves.length, 2));
    assert.equal(saves[0].operationId, saves[1].operationId);
    assert.equal((screen.getByRole("textbox", { name: "Name" }) as HTMLInputElement).value, "Keep me");
  } finally {
    cleanup();
  }
});

test("an existing view opens with its conditions and saves with its version", async () => {
  const { api, saves } = editorApi();
  const view: ViewDto = {
    id: "v9",
    name: "Due soon",
    icon: "flag",
    color: "red",
    pinned: false,
    position: 0,
    alertsEnabled: true,
    spaceId: null,
    version: "4",
    deleted: false,
    definitionJson: JSON.stringify({ v: 1, match: "all", items: [{ field: "due", op: "inRange", value: { relative: "next", days: 7 } }] }),
    layoutJson: model.layoutToJson({ ...model.defaultLayout, kind: "table" }),
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewEditor view={view} icons={icons} tags={tags} containers={[]} api={api} onSaved={() => undefined} onCancel={() => undefined} />,
    { container: dom.window.document.body },
  );
  try {
    assert.equal((screen.getByRole("textbox", { name: "Name" }) as HTMLInputElement).value, "Due soon");
    assert.ok(screen.getByRole("group", { name: "Condition on Due date" }));
    assert.equal((screen.getByRole("spinbutton", { name: "Number of days" }) as HTMLInputElement).value, "7");
    assert.ok(screen.getByRole("group", { name: "Table columns" }));
    await user.click(screen.getByRole("button", { name: "Save view" }));
    await waitFor(() => assert.equal(saves.length, 1));
    assert.equal(saves[0].viewId, "v9");
    assert.equal(saves[0].expectedVersion, "4");
    assert.equal(saves[0].alertsEnabled, true);
  } finally {
    cleanup();
  }
});

test("the GitHub conditions appear when a repository is connected and save the repository id", async () => {
  const { api, saves } = editorApi();
  const withGitHub = {
    ...api,
    gitHubOptions: async () => ({ ok: true, repositories: [{ id: "repo-1", fullName: "person/chaos" }], error: null }),
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewEditor
      view={null}
      icons={icons}
      tags={tags}
      containers={[]}
      api={withGitHub}
      onSaved={() => undefined}
      onCancel={() => undefined}
    />,
    { container: dom.window.document.body },
  );
  try {
    await user.type(screen.getByRole("textbox", { name: "Name" }), "My open GitHub bugs");
    await user.click(screen.getByRole("button", { name: "Add condition" }));
    const row = screen.getByRole("group", { name: "Condition on Status" });
    await waitFor(() => assert.ok(within(row).getByRole("option", { name: "GitHub repository" })));
    await user.selectOptions(within(row).getByRole("combobox", { name: "Field" }), "githubRepository");
    const repoRow = await screen.findByRole("group", { name: "Condition on GitHub repository" });
    await user.selectOptions(within(repoRow).getByRole("combobox", { name: "GitHub repository" }), "repo-1");
    await user.click(screen.getByRole("button", { name: "Add condition" }));
    const next = screen.getByRole("group", { name: "Condition on Status" });
    await user.selectOptions(within(next).getByRole("combobox", { name: "Field" }), "githubState");
    await user.click(screen.getByRole("button", { name: "Save view" }));
    await waitFor(() => assert.equal(saves.length, 1));
    const items = JSON.parse(saves[0].definitionJson).items;
    assert.deepEqual(items[0], { field: "githubRepository", op: "is", value: "repo-1" });
    assert.deepEqual(items[1], { field: "githubState", op: "is", value: "Open" });
  } finally {
    cleanup();
  }
});

test("without a GitHub connection the GitHub fields are not offered", async () => {
  const { api } = editorApi();
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewEditor view={null} icons={icons} tags={tags} containers={[]} api={api} onSaved={() => undefined} onCancel={() => undefined} />,
    { container: dom.window.document.body },
  );
  try {
    await user.click(screen.getByRole("button", { name: "Add condition" }));
    const row = screen.getByRole("group", { name: "Condition on Status" });
    assert.equal(within(row).queryByRole("option", { name: "GitHub repository" }), null);
  } finally {
    cleanup();
  }
});

// ---- The table ----------------------------------------------------------------------------------

function tableApi() {
  const edits: Parameters<typeof taskRpc.edit>[0][] = [];
  const statuses: Parameters<typeof taskRpc.setStatus>[0][] = [];
  return {
    edits,
    statuses,
    api: {
      edit: async (request: Parameters<typeof taskRpc.edit>[0]) => {
        edits.push(request);
        return {
          ok: true,
          task: task(request.taskId, request.title, {
            priority: request.priority,
            version: "2",
            dueDate: request.dueDate,
            estimateMinutes: request.estimateMinutes,
          }),
          error: null,
          operationId: request.operationId,
          warning: null,
        };
      },
      setStatus: async (request: Parameters<typeof taskRpc.setStatus>[0]) => {
        statuses.push(request);
        return {
          ok: true,
          task: task(request.taskId, "Alpha", { status: request.status, version: "2" }),
          error: null,
          operationId: request.operationId,
          warning: null,
        };
      },
    },
  };
}

test("table inline edits are the ordinary commands with the row's version, and sorting reorders the loaded rows", async () => {
  const { api, edits, statuses } = tableApi();
  const rows = [
    resultRow(task("a", "Alpha", { priority: "Low" })),
    resultRow(task("b", "Beta", { priority: "High", dueDate: "2026-05-01" })),
  ];
  const updated: TaskResponse[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewTable
      rows={rows}
      columns={["title", "status", "priority", "due", "estimate"]}
      tags={tags}
      busy={false}
      api={api}
      onOpen={() => undefined}
      onTaskUpdated={(item) => updated.push(item)}
    />,
    { container: dom.window.document.body },
  );
  try {
    const titles = () =>
      screen
        .getAllByRole("button")
        .filter((button) => button.getAttribute("data-slot") === "row-title")
        .map((button) => button.textContent);
    assert.deepEqual(titles(), ["Alpha", "Beta"]);
    await user.click(screen.getByRole("button", { name: "Priority" }));
    assert.deepEqual(titles(), ["Alpha", "Beta"]); // ascending: Low before High
    await user.click(screen.getByRole("button", { name: "Priority" }));
    assert.deepEqual(titles(), ["Beta", "Alpha"]); // descending

    await user.selectOptions(screen.getByRole("combobox", { name: "Status of Alpha" }), "InProgress");
    await waitFor(() => assert.equal(statuses.length, 1));
    assert.equal(statuses[0].taskId, "a");
    assert.equal(statuses[0].expectedVersion, "1");
    assert.equal(statuses[0].status, "InProgress");

    await user.selectOptions(screen.getByRole("combobox", { name: "Priority of Alpha" }), "Urgent");
    await waitFor(() => assert.equal(edits.length, 1));
    assert.equal(edits[0].priority, "Urgent");
    assert.equal(edits[0].expectedVersion, "1");
    assert.equal(edits[0].title, "Alpha");

    await user.click(screen.getByRole("button", { name: "Edit title of Beta" }));
    const input = screen.getByRole("textbox", { name: "Title of Beta" });
    await user.clear(input);
    await user.type(input, "Beta renamed{Enter}");
    await waitFor(() => assert.equal(edits.length, 2));
    assert.equal(edits[1].title, "Beta renamed");
    assert.equal(edits[1].dueDate, "2026-05-01"); // the other fields travel unchanged
    assert.equal(updated.length, 3);
  } finally {
    cleanup();
  }
});

test("a refused inline edit shows the host's message and leaves the row alone", async () => {
  const api = {
    edit: async () => ({
      ok: false,
      task: null,
      operationId: "x",
      warning: null,
      error: { kind: "VersionConflict", message: "This task changed elsewhere. Refresh and try again.", fields: [], correlationId: "x" },
    }),
    setStatus: async () => ({
      ok: false,
      task: null,
      operationId: "x",
      warning: null,
      error: { kind: "VersionConflict", message: "This task changed elsewhere. Refresh and try again.", fields: [], correlationId: "x" },
    }),
  };
  const updated: TaskResponse[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewTable
      rows={[resultRow(task("a", "Alpha"))]}
      columns={["title", "priority"]}
      tags={tags}
      busy={false}
      api={api}
      onOpen={() => undefined}
      onTaskUpdated={(item) => updated.push(item)}
    />,
    { container: dom.window.document.body },
  );
  try {
    await user.selectOptions(screen.getByRole("combobox", { name: "Priority of Alpha" }), "High");
    await screen.findByText("This task changed elsewhere. Refresh and try again.");
    assert.equal(updated.length, 0);
  } finally {
    cleanup();
  }
});

test("the table's tag cell opens the tag picker for that one task and closes it again", async () => {
  const asked: { targetKind: string; targetId: string }[] = [];
  const tagApi = {
    ...tagRpc,
    targetTags: async (request: { targetKind: string; targetId: string }) => {
      asked.push(request);
      return { ok: true, tagIds: ["bug"], error: null };
    },
    suggest: async () => ({ ok: true, tags: [], error: null }),
  };
  const row = { ...resultRow(task("a", "Alpha")), tagIds: ["bug"] };
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <ViewTable
      rows={[row]}
      columns={["title", "tags"]}
      tags={tags}
      busy={false}
      tagApi={tagApi}
      onOpen={() => undefined}
      onTaskUpdated={() => undefined}
    />,
    { container: dom.window.document.body },
  );
  try {
    assert.equal(asked.length, 0, "no picker, so no tag reads, until a cell is edited");
    await user.click(screen.getByRole("button", { name: "Edit tags of Alpha" }));
    await waitFor(() => assert.deepEqual(asked, [{ targetKind: "Task", targetId: "a" }]));
    await user.click(screen.getByRole("button", { name: "Done editing tags" }));
    assert.ok(screen.getByRole("button", { name: "Edit tags of Alpha" }));
  } finally {
    cleanup();
  }
});

// ---- Alerts -------------------------------------------------------------------------------------

function alertApi(
  alerts: { id: string; viewId: string; viewName: string; viewAvailable: boolean; taskCount: number; firstTaskTitle: string }[],
) {
  const acknowledged: string[] = [];
  let current = alerts;
  const api = {
    alerts: async () => ({ ok: true, alerts: current.map((item) => ({ ...item, deliveredAtUtc: "2026-11-10T09:00:00Z" })), error: null }),
    acknowledgeAlert: async (request: { alertId: string }) => {
      acknowledged.push(request.alertId);
      current = current.filter((item) => item.id !== request.alertId);
      return { ok: true, error: null };
    },
  };
  return { api, acknowledged };
}

test("a view alert says how many tasks are new, opens the view and is acknowledged", async () => {
  const { ViewAlertStack } = await import("./ViewAlerts");
  const { api, acknowledged } = alertApi([
    { id: "a1", viewId: "v1", viewName: "My open bugs", viewAvailable: true, taskCount: 3, firstTaskTitle: "Crash on save" },
  ]);
  const opened: string[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<ViewAlertStack api={api} onOpen={(id) => opened.push(id)} />, { container: dom.window.document.body });
  try {
    const card = await screen.findByRole("article", { name: "View alert: My open bugs" });
    assert.match(card.textContent ?? "", /3 new tasks in My open bugs/);
    assert.match(card.textContent ?? "", /Crash on save and 2 more/);
    await user.click(within(card).getByRole("button", { name: "Open view" }));
    await waitFor(() => assert.deepEqual(acknowledged, ["a1"]));
    assert.deepEqual(opened, ["v1"]);
    await waitFor(() => assert.equal(screen.queryByRole("article", { name: "View alert: My open bugs" }), null));
  } finally {
    cleanup();
  }
});

test("dismissing a view alert acknowledges it without opening anything, and a deleted view has no Open button", async () => {
  const { ViewAlertStack } = await import("./ViewAlerts");
  const { api, acknowledged } = alertApi([
    { id: "a2", viewId: "v2", viewName: "Gone view", viewAvailable: false, taskCount: 1, firstTaskTitle: "Single" },
  ]);
  const opened: string[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<ViewAlertStack api={api} onOpen={(id) => opened.push(id)} />, { container: dom.window.document.body });
  try {
    const card = await screen.findByRole("article", { name: "View alert: Gone view" });
    assert.match(card.textContent ?? "", /1 new task in Gone view/);
    assert.equal(within(card).queryByRole("button", { name: "Open view" }), null);
    await user.click(within(card).getByRole("button", { name: "Dismiss alert for Gone view" }));
    await waitFor(() => assert.deepEqual(acknowledged, ["a2"]));
    assert.deepEqual(opened, []);
  } finally {
    cleanup();
  }
});

// ---- The sidebar --------------------------------------------------------------------------------

function appApi(views: ViewDto[], counts: { viewId: string; count: number }[]) {
  return {
    taskRpc: {
      ...taskRpc,
      listInbox: async () => ({ ok: true, tasks: [], error: null }),
      listDeleted: async () => ({ ok: true, tasks: [], error: null }),
    },
    containerRpc: {
      ...containerRpc,
      list: async () => ({ ok: true, containers: [], error: null }),
      listDeleted: async () => ({ ok: true, containers: [], error: null }),
    },
    tagRpc: {
      ...tagRpc,
      list: async () => ({ ok: true, tags: [], error: null }),
      listDeleted: async () => ({ ok: true, tags: [], error: null }),
      targetTags: async () => ({ ok: true, tagIds: [], error: null }),
      suggest: async () => ({ ok: true, tags: [], error: null }),
    },
    queryRpc: {
      ...queryRpc,
      counts: async () => ({ ok: true, views: [], tags: [], containers: [], error: null }),
      run: async () => ({ ok: true, rows: [], totalCount: 0, nextCursor: null, error: null }),
    },
    viewRpc: {
      ...viewRpc,
      list: async () => ({ ok: true, views, icons, error: null }),
      counts: async () => ({ ok: true, counts, error: null }),
      run: async () => ({
        ok: true,
        rows: [resultRow(task("a", "Matching task"))],
        totalCount: 1,
        nextCursor: null,
        layoutJson: model.layoutToJson(model.defaultLayout),
        error: null,
      }),
    },
  };
}

function savedView(id: string, name: string, pinned: boolean): ViewDto {
  return {
    id,
    name,
    icon: "bug",
    color: "blue",
    pinned,
    position: 0,
    definitionJson: '{"v":1,"match":"all","items":[]}',
    layoutJson: model.layoutToJson(model.defaultLayout),
    alertsEnabled: false,
    spaceId: null,
    version: "1",
    deleted: false,
  };
}

test("only pinned views appear under Views, with their count, and opening one lists its rows", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <App
      initialView="tasks"
      templates={<></>}
      api={appApi([savedView("p", "Open bugs", true), savedView("q", "Hidden view", false)], [{ viewId: "p", count: 5 }])}
    />,
    { container: dom.window.document.body },
  );
  try {
    const nav = await screen.findByRole("navigation", { name: "Views" });
    const pinned = await within(nav).findByRole("button", { name: /Open bugs/ });
    assert.match(pinned.textContent ?? "", /5/);
    assert.equal(within(nav).queryByRole("button", { name: /Hidden view/ }), null);
    await user.click(pinned);
    await screen.findByRole("button", { name: "Matching task" });
    assert.ok(screen.getByRole("heading", { name: "Open bugs" }));
  } finally {
    cleanup();
  }
});

test("sharing a personal view copies it into the chosen editable space", async () => {
  const original = { ...savedView("private", "My view", true), spaceId: "personal" };
  const base = appApi([original], []);
  const shares: { viewId: string; spaceId: string }[] = [];
  const api = {
    ...base,
    containerRpc: { ...base.containerRpc, listForSpace: async () => ({ ok: true, containers: [], error: null }) },
    spaceRpc: {
      ...spaceRpc,
      list: async () => ({
        ok: true,
        error: null,
        spaces: [
          { spaceId: "personal", name: "Personal", kind: "Personal", role: null, memberCount: 1 },
          { spaceId: "team", name: "Team", kind: "Shared", role: 2, memberCount: 2 },
        ],
      }),
    },
    viewRpc: {
      ...base.viewRpc,
      share: async (request: { operationId: string; viewId: string; spaceId: string }) => {
        shares.push(request);
        return { ok: true, view: { ...original, id: "copy", spaceId: request.spaceId }, error: null };
      },
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" templates={<></>} api={api} />, { container: dom.window.document.body });
  try {
    await user.click(await screen.findByRole("button", { name: /My view/ }));
    const selector = await screen.findByLabelText("Share with space");
    await user.selectOptions(selector, "team");
    await waitFor(() => assert.equal(shares.length, 1));
    assert.equal(shares[0].viewId, "private");
    assert.equal(shares[0].spaceId, "team");
    assert.ok(screen.getByRole("heading", { name: "My view" }));
  } finally {
    cleanup();
  }
});

test("an open shared board reloads after a remote activity notification without focus", async () => {
  const { fakeActivity } = await import("./activityTestApi");
  const { configureActivity } = await import("./activity");
  const feed = fakeActivity();
  configureActivity(feed.api);
  let column = "NotStarted";
  const getView: typeof boardRpc.getView = async () => ({
    ok: true,
    error: null,
    settings: {
      groupBy: "Status",
      swimlaneBy: "None",
      columnTagId: null,
      swimlaneTagId: null,
      wipLimitsJson: "{}",
      hiddenColumnsJson: "[]",
      version: "1",
      isDefault: false,
    },
    columns: [
      { key: "NotStarted", title: "Not started", isNone: false },
      { key: "InProgress", title: "In progress", isNone: false },
    ],
    lanes: [{ key: "", title: "All tasks", isNone: true }],
    cards: [
      {
        task: task("a", "Shared card", { status: column }),
        tagIds: [],
        subtasksTotal: 0,
        subtasksDone: 0,
        hasIssueLink: false,
        columnKey: column,
        laneKey: "",
      },
    ],
    totalCount: 1,
    truncated: false,
  });
  const original = { ...savedView("shared", "Shared board", true), spaceId: "team" };
  const base = appApi([original], []);
  const api = {
    ...base,
    boardRpc: { ...boardRpc, getView },
    viewRpc: {
      ...base.viewRpc,
      run: async () => ({
        ok: true,
        rows: [],
        totalCount: 0,
        nextCursor: null,
        layoutJson: model.layoutToJson({ ...model.defaultLayout, kind: "board" }),
        error: null,
      }),
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" templates={<></>} api={api} />, { container: dom.window.document.body });
  try {
    await user.click(await screen.findByRole("button", { name: /Shared board/ }));
    await waitFor(() => assert.ok(within(screen.getByLabelText("Not started cards")).getByLabelText("Shared card")));
    await waitFor(() => assert.equal(feed.subscribed, true));
    column = "InProgress";
    feed.push(["task"], "sync", "sync.apply");
    feed.emit();
    await waitFor(() => assert.ok(within(screen.getByLabelText("In progress cards")).getByLabelText("Shared card")));
  } finally {
    cleanup();
    configureActivity();
  }
});
