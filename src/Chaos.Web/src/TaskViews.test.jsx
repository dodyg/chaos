import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
// The shadcn primitives these views share with the desktop resolve through the aliases in tsconfig.json, as in vite.config.js.
import { ListPicker, TaskDetail, TaskList } from "./TaskViews.jsx";
import { assertServerShape } from "./contractSupport.js";

globalThis.BroadcastChannel = undefined;
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "https://example.test/app/#lists" });
Object.assign(globalThis, {
  window: dom.window,
  document: dom.window.document,
  HTMLElement: dom.window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true,
});

const ids = {
  personal: "00000000-0000-4000-8000-000000000001",
  studio: "00000000-0000-4000-8000-000000000002",
  inbox: "00000000-0000-4000-8000-000000000003",
  planning: "00000000-0000-4000-8000-000000000004",
  task: "00000000-0000-4000-8000-000000000005",
  omar: "00000000-0000-4000-8000-000000000006",
  me: "00000000-0000-4000-8000-000000000007",
  entry: "00000000-0000-4000-8000-000000000008",
  removed: "00000000-0000-4000-8000-000000000009",
};

const list = (spaceId, id, name, extra = {}) => ({ spaceId, id, name, isInbox: false, position: 0, isArchived: false, ...extra });
const space = (id, name, extra = {}) => ({
  id,
  name,
  isShared: false,
  role: null,
  canChange: true,
  colorKey: null,
  icon: null,
  lists: [],
  ...extra,
});
const person = (id, name, isMe = false) => ({ memberId: id, name, isMe });
const update = (id, author, markdown, extra = {}) => ({
  id,
  markdown,
  author,
  isMine: author === "You",
  at: "2026-11-24T09:00:00.000Z",
  removedByOwner: false,
  ...extra,
});
const task = (extra = {}) => ({
  id: ids.task,
  listId: ids.planning,
  spaceId: ids.studio,
  title: "Book the studio",
  notes: null,
  status: 0,
  plannedDate: null,
  dueDate: null,
  isRecurring: false,
  eachMustComplete: false,
  priority: 2,
  position: 0,
  plannedTime: null,
  plannedZoneId: null,
  dueTime: null,
  dueZoneId: null,
  isOnMyDay: false,
  isTerminal: false,
  canChange: false,
  assignees: [],
  updates: [],
  ...extra,
});

const spaces = [
  space(ids.personal, "Personal", { lists: [list(ids.personal, ids.inbox, "Inbox", { isInbox: true })] }),
  space(ids.studio, "Studio", {
    isShared: true,
    role: 3,
    canChange: false,
    lists: [list(ids.studio, ids.planning, "Planning")],
  }),
];
for (const group of spaces) {
  assertServerShape("WebSpace", group);
  group.lists.forEach((item) => assertServerShape("WebList", item));
}
assertServerShape("WebPerson", person(ids.omar, "Omar"));
assertServerShape("WebUpdate", update(ids.entry, "Omar", "Booked for Friday"));

async function render(element) {
  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(element);
  });
  return {
    host,
    close: async () => {
      await act(async () => root.unmount());
      host.remove();
    },
  };
}

test("lists are grouped by space and a shared space shows the caller's role", async () => {
  const view = await render(<ListPicker spaces={spaces} />);
  try {
    const headings = [...view.host.querySelectorAll("h2")].map((heading) => heading.textContent);
    assert.deepEqual(headings, ["Personal 1", "Studio 1"]);
    assert.match(view.host.textContent, /Shared space · You are a viewer of this space/);
    const links = [...view.host.querySelectorAll("a.list-row")].map((link) => link.getAttribute("href"));
    assert.deepEqual(links, [`#list/${ids.inbox}`, `#list/${ids.planning}`]);
  } finally {
    await view.close();
  }
});

test("a viewer's list is read-only while an editor's list can be completed", async () => {
  const acted = [];
  const route = { href: `#list/${ids.planning}` };
  const viewer = await render(<TaskList tasks={[task()]} route={route} canWrite act={(item, action) => acted.push([item.id, action])} />);
  try {
    const check = viewer.host.querySelector("button.task-check");
    assert.equal(check.disabled, true);
    assert.equal(check.getAttribute("aria-label"), "Read only");
    assert.equal(check.getAttribute("title"), "Viewers cannot change tasks");
  } finally {
    await viewer.close();
  }

  assertServerShape(
    "WebTask",
    task({ canChange: true, assignees: [person(ids.omar, "Omar"), person(ids.me, "Mina", true)], dueDate: "2026-11-26" }),
  );
  const editor = await render(
    <TaskList
      tasks={[task({ canChange: true, assignees: [person(ids.omar, "Omar"), person(ids.me, "Mina", true)], dueDate: "2026-11-26" })]}
      route={route}
      canWrite
      act={(item, action) => acted.push([item.id, action])}
    />,
  );
  try {
    const check = editor.host.querySelector("button.task-check");
    assert.equal(check.disabled, false);
    assert.equal(check.getAttribute("aria-label"), "Complete Book the studio");
    assert.equal(editor.host.querySelector(".row-title small span").textContent, "Omar, You");
    const dueDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" }).format(
      new Date("2026-11-26T00:00:00Z"),
    );
    assert.equal(editor.host.querySelector(".due-date").textContent, `Due ${dueDate}`);
    await act(async () => {
      check.click();
    });
    assert.deepEqual(acted, [[ids.task, "complete"]]);
  } finally {
    await editor.close();
  }
});

test("a task whose assignees must each complete it is completed on the desktop; reopening and My Day stay here", async () => {
  const acted = [];
  const route = { href: `#list/${ids.planning}` };
  const studio = { ...spaces[1], role: 2, canChange: true };
  const each = (extra = {}) =>
    task({ canChange: true, eachMustComplete: true, assignees: [person(ids.omar, "Omar"), person(ids.me, "Mina", true)], ...extra });
  const onAct = (item, action) => acted.push(action);
  assertServerShape("WebTask", each());
  const list = await render(<TaskList tasks={[each()]} route={route} canWrite act={onAct} />);
  try {
    const check = list.host.querySelector("button.task-check");
    assert.equal(check.disabled, true);
    assert.equal(check.getAttribute("aria-label"), "Complete on desktop");
    assert.equal(check.getAttribute("title"), "Complete on desktop");
  } finally {
    await list.close();
  }

  const open = await render(
    <TaskDetail task={each()} list={studio.lists[0]} space={studio} back={route.href} canWrite offline={false} act={onAct} />,
  );
  try {
    assert.match(open.host.querySelector(".task-actions .inline-hint").textContent, /Each assignee completes their part on desktop/);
    const [myDay] = open.host.querySelectorAll(".task-actions button");
    assert.equal(myDay.textContent, "Add to My Day");
    assert.equal(myDay.disabled, false);
    await act(async () => {
      myDay.click();
    });
    assert.deepEqual(acted, ["add-to-my-day"]);
  } finally {
    await open.close();
  }

  const done = await render(
    <TaskDetail
      task={each({ status: 4, isTerminal: true })}
      list={studio.lists[0]}
      space={studio}
      back={route.href}
      canWrite
      offline={false}
      act={onAct}
    />,
  );
  try {
    assert.equal(done.host.querySelector(".task-actions .inline-hint"), null);
    const [reopen] = done.host.querySelectorAll(".task-actions button");
    assert.equal(reopen.textContent, "Reopen task");
    assert.equal(reopen.disabled, false);
    await act(async () => {
      reopen.click();
    });
    assert.deepEqual(acted, ["add-to-my-day", "reopen"]);
  } finally {
    await done.close();
  }
});

test("a shared task shows its space, assignees and the authors of its updates; a viewer can only add it to My Day", async () => {
  const acted = [];
  const shared = task({
    canChange: false,
    assignees: [person(ids.omar, "Omar"), person(ids.me, "Mina", true)],
    updates: [
      update(ids.entry, "Omar", "**Booked for Friday**"),
      update(ids.removed, "Omar", "", { removedByOwner: true }),
      update(ids.task, "You", "Sending the deposit."),
    ],
  });
  assertServerShape("WebTask", shared);
  const view = await render(
    <TaskDetail
      task={shared}
      list={spaces[1].lists[0]}
      space={spaces[1]}
      back={`#list/${ids.planning}`}
      canWrite
      offline={false}
      act={(item, action) => acted.push(action)}
    />,
  );
  try {
    const fields = Object.fromEntries(
      [...view.host.querySelectorAll(".task-fields > div")].map((field) => [
        field.querySelector("dt").textContent,
        field.querySelector("dd").textContent,
      ]),
    );
    assert.deepEqual(fields, {
      Status: "Not started",
      List: "Planning",
      Space: "Studio",
      Assignees: "Omar, You",
      Planned: "—",
      Due: "—",
    });
    assert.match(view.host.querySelector(".notice").textContent, /You are a viewer of this space/);
    const authors = [...view.host.querySelectorAll(".task-updates header strong")].map((author) => author.textContent);
    assert.deepEqual(authors, ["Omar", "Omar", "You"]);
    assert.match(view.host.querySelector(".task-updates li.removed").textContent, /Removed by an owner/);
    assert.equal(view.host.querySelector(".task-updates strong").textContent, "Omar");
    assert.match(view.host.querySelector(".task-updates .markdown-notes").textContent, /Booked for Friday/);
    const [complete, myDay] = view.host.querySelectorAll(".task-actions button");
    assert.equal(complete.disabled, true);
    assert.equal(complete.textContent, "Complete task");
    assert.equal(myDay.disabled, false);
    assert.equal(myDay.textContent, "Add to My Day");
    await act(async () => {
      myDay.click();
    });
    assert.deepEqual(acted, ["add-to-my-day"]);
  } finally {
    await view.close();
  }
});

test("a personal task has no space or assignee row and can be completed", async () => {
  const acted = [];
  const personal = task({ spaceId: ids.personal, listId: ids.inbox, canChange: true });
  assertServerShape("WebTask", personal);
  const view = await render(
    <TaskDetail
      task={personal}
      list={spaces[0].lists[0]}
      space={spaces[0]}
      back="#my-day"
      canWrite
      offline={false}
      act={(item, action) => acted.push(action)}
    />,
  );
  try {
    assert.equal(view.host.querySelector(".notice"), null);
    assert.match(view.host.querySelector(".back").textContent, /My Day/);
    assert.equal(
      [...view.host.querySelectorAll("dt")].some((term) => term.textContent === "Assignees"),
      false,
    );
    assert.match(view.host.querySelector(".task-updates").textContent, /No updates yet/);
    const [complete] = view.host.querySelectorAll(".task-actions button");
    assert.equal(complete.disabled, false);
    await act(async () => {
      complete.click();
    });
    assert.deepEqual(acted, ["complete"]);
  } finally {
    await view.close();
  }
});
