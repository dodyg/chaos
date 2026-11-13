import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, diaryRpc, gitHubRpc, historyRpc, markdownRpc, queryRpc, recurrenceRpc, tagRpc, taskRpc, undoRpc, type TaskResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  CustomEvent: { value: dom.window.CustomEvent, configurable: true },
  NodeFilter: { value: dom.window.NodeFilter, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
  KeyboardEvent: { value: dom.window.KeyboardEvent, configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { MarkdownPreview, extractChecklist, htmlToMarkdown, internalLinkIds } = await import("./markdown");
const { formatEdit, linkQueryAt, padLinkMarkdown, taskLinkMarkdown } = await import("./markdownEditing");
const { MarkdownEditor } = await import("./MarkdownEditor");
const { App } = await import("./App");

const taskId = "0f8fad5b-d9cb-469f-a165-70867728950e";

test("GFM renders headings, emphasis, strike, tables, task lists and highlighted code as elements", () => {
  const source = [
    "# Plan", "", "Some **bold**, *italic* and ~~gone~~ text with `code`.", "",
    "| Item | Qty |", "| :--- | ---: |", "| Eggs | 12 |", "",
    "- [x] Buy milk", "- [ ] Call Sam", "", "> quoted", "",
    "```ts", "const answer: number = 42;", "```",
  ].join("\n");
  const { container } = render(<MarkdownPreview source={source}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  assert.equal(container.querySelector("h3")?.textContent, "Plan");
  assert.equal(container.querySelector("strong")?.textContent, "bold");
  assert.equal(container.querySelector("em")?.textContent, "italic");
  assert.equal(container.querySelector("del")?.textContent, "gone");
  assert.equal(container.querySelector("th")?.textContent, "Item");
  assert.equal((container.querySelectorAll("td")[1] as HTMLElement).style.textAlign, "right");
  const boxes = [...container.querySelectorAll("input[type=checkbox]")] as HTMLInputElement[];
  assert.deepEqual(boxes.map(box => box.checked), [true, false]);
  assert.ok(boxes.every(box => box.disabled));
  assert.ok(container.querySelector("blockquote"));
  assert.ok(container.querySelector("pre code .hljs-keyword"), "code is highlighted locally");
  cleanup();
});

test("scripts, raw HTML, unsafe links and images never become live elements", async () => {
  const opened: string[] = [];
  const source = [
    "<script>window.hacked = true</script>",
    "", "<img src=x onerror=alert(1)> inline <b onclick=alert(1)>b</b>",
    "", "[bad](javascript:alert(1)) [file](file:///C:/x) [web](https://example.org/page)",
    "", "![cat](https://example.org/cat.png)",
  ].join("\n");
  const { container } = render(<MarkdownPreview source={source} links={{ onOpenExternal: url => opened.push(url) }}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  assert.equal(container.querySelector("script"), null);
  assert.equal(container.querySelector("img"), null);
  assert.equal(container.querySelector("a"), null, "no anchor can navigate the WebView");
  assert.equal(container.querySelector("[onclick],[onerror]"), null);
  assert.match(container.textContent ?? "", /<script>window\.hacked = true<\/script>/);
  assert.equal((globalThis as { hacked?: boolean }).hacked, undefined);
  assert.ok(screen.getByRole("img", { name: "Image not loaded: cat" }));
  assert.equal(screen.queryByRole("button", { name: "bad" }), null);
  assert.equal(screen.queryByRole("button", { name: "file" }), null);
  fireEvent.click(screen.getByRole("button", { name: "web" }));
  assert.deepEqual(opened, ["https://example.org/page"]);
  cleanup();
});

test("HTML image tags render safely and fit the preview", () => {
  const source = '<img width="447" height="624" alt="Screenshot" src="https://github.com/user-attachments/assets/example" onerror="alert(1)" />';
  const { container } = render(<MarkdownPreview source={source}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  const image = screen.getByRole("img", { name: "Screenshot" }) as HTMLImageElement;
  assert.equal(image.getAttribute("src"), "https://github.com/user-attachments/assets/example");
  assert.equal(image.width, 447);
  assert.equal(image.height, 624);
  assert.ok(image.classList.contains("max-w-full"));
  assert.equal(container.querySelector("[onerror]"), null);
  cleanup();

  const blocked = render(<MarkdownPreview source={'<img src="file:///C:/secret" alt="Private" />'}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  assert.equal(blocked.container.querySelector("img"), null);
  cleanup();
});

test("an image opens in a centered viewer and closes with Escape", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  render(<MarkdownPreview source={'<img src="https://example.org/image.png" alt="Screenshot" />'}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  await user.click(screen.getByRole("button", { name: "Enlarge Screenshot" }));
  const viewer = screen.getByRole("dialog", { name: "Screenshot" });
  assert.equal((within(viewer).getByRole("img", { name: "Screenshot" }) as HTMLImageElement).getAttribute("src"), "https://example.org/image.png");
  await user.keyboard("{Escape}");
  await waitFor(() => assert.equal(screen.queryByRole("dialog", { name: "Screenshot" }), null));
  cleanup();
});

test("a private GitHub image retries through the task loader and reports failures", async () => {
  const url = "https://github.com/user-attachments/assets/36da6f97-9797-4896-a07d-a5f92975d8e8";
  const requested: string[] = [];
  const links = { loadGitHubImage: async (value: string) => { requested.push(value); return { dataUrl: "data:image/png;base64,AQID" }; } };
  render(<MarkdownPreview source={`<img src="${url}" alt="Screenshot" />`} links={links}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  fireEvent.error(screen.getByRole("img", { name: "Screenshot" }));
  await waitFor(() => assert.equal((screen.getByRole("img", { name: "Screenshot" }) as HTMLImageElement).getAttribute("src"), "data:image/png;base64,AQID"));
  assert.deepEqual(requested, [url]);
  cleanup();
  const opened: string[] = [];
  render(<MarkdownPreview source={`<img src="${url}" alt="Screenshot" />`} links={{ loadGitHubImage: async () => ({ error: "Access denied", issueUrl: "https://github.com/a/b/issues/1" }), onOpenExternal: value => opened.push(value) }}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  fireEvent.error(screen.getByRole("img", { name: "Screenshot" }));
  await screen.findByText("Access denied");
  fireEvent.click(screen.getByRole("button", { name: "Open issue on GitHub" }));
  assert.deepEqual(opened, ["https://github.com/a/b/issues/1"]);
  cleanup();
});

test("internal links resolve by ID: a renamed task shows its new title, a deleted one is a broken link", async () => {
  const source = `[Old name](chaos:task/${taskId}) and [Gone](chaos:task/11111111-2222-3333-4444-555555555555)`;
  assert.deepEqual(internalLinkIds(source).sort(), [taskId, "11111111-2222-3333-4444-555555555555"].sort());
  const openedTasks: string[] = [];
  render(<MarkdownPreview source={source} links={{
    resolve: async () => [{ id: taskId, title: "New name", deleted: false }, { id: "11111111-2222-3333-4444-555555555555", title: "Gone", deleted: true }],
    onOpenTask: id => openedTasks.push(id),
  }}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  const renamed = await screen.findByRole("button", { name: "New name" });
  fireEvent.click(renamed);
  assert.deepEqual(openedTasks, [taskId]);
  await screen.findByText("(deleted task)");
  assert.equal(screen.queryByRole("button", { name: "Gone" }), null);
  cleanup();
});

test("checklist items are read from the AST with occurrence keys, in document order", () => {
  const items = extractChecklist("- [ ] Passport\n- [x] **Charger**\n  - [ ] Cable\n- [ ] Passport\n- not a task\n\n```\n- [ ] in code\n```");
  assert.deepEqual(items, [
    { itemKey: "Passport#0", title: "Passport", done: false },
    { itemKey: "Charger#0", title: "Charger", done: true },
    { itemKey: "Cable#0", title: "Cable", done: false },
    { itemKey: "Passport#1", title: "Passport", done: false },
  ]);
});

test("toolbar transforms act on the selection and toggle off", () => {
  assert.deepEqual(formatEdit("say hello now", 4, 9, "bold"), { start: 4, end: 9, insert: "**hello**", selectionStart: 6, selectionEnd: 11 });
  assert.equal(formatEdit("say **hello** now", 4, 13, "bold").insert, "hello");
  assert.equal(formatEdit("one\ntwo", 0, 7, "bullets").insert, "- one\n- two");
  assert.equal(formatEdit("- one\n- two", 0, 11, "bullets").insert, "one\ntwo");
  assert.equal(formatEdit("a\nb", 0, 3, "numbers").insert, "1. a\n2. b");
  assert.equal(formatEdit("buy", 0, 3, "checklist").insert, "- [ ] buy");
  const link = formatEdit("docs", 0, 4, "link");
  assert.equal(link.insert, "[docs](https://)");
  assert.equal("[docs](https://)".slice(link.selectionStart, link.selectionEnd), "https://");
  assert.deepEqual(linkQueryAt("see [[pla", 9), { start: 4, query: "pla" });
  assert.equal(linkQueryAt("see [[pla]] x", 13), null);
  assert.equal(taskLinkMarkdown("a [b]", taskId), `[a \\[b\\]](chaos:task/${taskId})`);
});

test("rich HTML paste becomes GFM without script content", () => {
  const markdown = htmlToMarkdown("<h2>Trip</h2><p><b>Pack</b> <i>light</i><script>steal()</script></p><ul><li>socks</li></ul><table><tr><th>A</th></tr><tr><td>1</td></tr></table><p><del>old</del></p>");
  assert.match(markdown, /^## Trip/);
  assert.match(markdown, /\*\*Pack\*\* \*light\*/);
  assert.match(markdown, /- {1,3}socks/);
  assert.match(markdown, /\| A \|/);
  assert.match(markdown, /~+old~+/);
  assert.doesNotMatch(markdown, /steal/);
});

function Harness({ search }: { search?: (query: string) => Promise<{ id: string; title: string }[]> }) {
  const [value, setValue] = React.useState("say hello");
  return <><MarkdownEditor id="notes" label="Notes" value={value} onChange={setValue} searchLinks={search}/><output data-testid="value">{value}</output></>;
}

test("the editor applies Ctrl+B to the selection, converts pasted HTML and completes [[ task links", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  render(<Harness search={async query => query.startsWith("pl") ? [{ id: taskId, title: "Plan the trip" }] : []}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  const area = screen.getByLabelText("Notes") as HTMLTextAreaElement;
  area.focus();
  area.setSelectionRange(4, 9);
  fireEvent.keyDown(area, { key: "b", ctrlKey: true });
  await waitFor(() => assert.equal(screen.getByTestId("value").textContent, "say **hello**"));

  area.setSelectionRange(area.value.length, area.value.length);
  fireEvent.paste(area, { clipboardData: { getData: (type: string) => type === "text/html" ? "<p> and <b>more</b></p>" : " and more" } });
  await waitFor(() => assert.equal(screen.getByTestId("value").textContent, "say **hello**and **more**"));

  await user.type(area, " [[[[pl"); // user-event: "[[" types one literal "["
  const option = await screen.findByRole("option", { name: "Plan the trip" });
  assert.ok(option);
  await user.keyboard("{Enter}");
  await waitFor(() => assert.match(screen.getByTestId("value").textContent ?? "", new RegExp(`\\[Plan the trip\\]\\(chaos:task/${taskId}\\)$`)));

  await user.click(within(screen.getByRole("group", { name: "Notes view" })).getByRole("button", { name: "Preview" }));
  assert.ok(screen.getByRole("button", { name: "Plan the trip" }));
  cleanup();
});

function task(id: string, title: string, notes = ""): TaskResponse {
  return { id, title, spaceId: "space", containerId: "inbox", notes, status: "NotStarted", plannedDate: null, dueDate: null, version: "3", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null };
}

test("app undo: the newest change is offered, Ctrl+Z outside text fields undoes it, inside a field it does not", async () => {
  const undone: string[] = [];
  const inbox = [task(taskId, "Pack", "- [ ] Passport\n- [x] Charger")];
  const converted: { items: readonly { itemKey: string; title: string; done: boolean }[] }[] = [];
  const api = {
    taskRpc: { ...taskRpc,
      listInbox: async () => ({ ok: true, tasks: inbox, error: null }),
      listDeleted: async () => ({ ok: true, tasks: [], error: null }),
      get: async () => ({ ok: true, task: inbox[0], error: null }),
      delete: async () => ({ ok: true, operationId: "op", task: inbox[0], error: null, warning: null }),
    },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
    recurrenceRpc: { ...recurrenceRpc, get: async () => ({ ok: true, rule: null, ended: false, occurrences: [], error: null }) },
    tagRpc: { ...tagRpc, list: async () => ({ ok: true, tags: [], error: null }), listDeleted: async () => ({ ok: true, tags: [], error: null }), targetTags: async () => ({ ok: true, tagIds: [], error: null }) },
    queryRpc: { ...queryRpc, counts: async () => ({ ok: true, views: [], tags: [], containers: [], error: null }) },
    undoRpc: { ...undoRpc,
      list: async () => ({ ok: true, entries: undone.length ? [] : [{ operationId: "op-delete", label: "Delete task “Pack”", appliedAtUtc: "2026-11-10T09:00:00.0000000Z" }], error: null }),
      undo: async (request: { targetOperationId: string }) => { undone.push(request.targetOperationId); return { ok: true, label: "Delete task “Pack”", error: null }; },
    },
    markdownRpc: { ...markdownRpc,
      resolveLinks: async () => ({ ok: true, targets: [], error: null }),
      convertChecklist: async (request: { items: readonly { itemKey: string; title: string; done: boolean }[] }) => { converted.push(request); return { ok: true, created: [], alreadyConverted: 0, error: null }; },
    },
    diaryRpc: { ...diaryRpc, timeline: async () => ({ ok: true, items: [], error: null }), list: async () => ({ ok: true, entries: [], error: null }) },
    historyRpc,
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" api={api} templates={<></>}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  const row = await screen.findByRole("button", { name: "Pack" });
  await user.click(screen.getByRole("button", { name: "Delete Pack" }));
  const toast = await screen.findByRole("status", { name: "Undo" });
  assert.match(toast.textContent ?? "", /Delete task “Pack”/);

  // In a text field Ctrl+Z belongs to the field.
  const capture = screen.getByLabelText("Add a task");
  capture.focus();
  fireEvent.keyDown(capture, { key: "z", ctrlKey: true });
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.deepEqual(undone, []);

  capture.blur();
  fireEvent.keyDown(dom.window.document.body, { key: "z", ctrlKey: true });
  await waitFor(() => assert.deepEqual(undone, ["op-delete"]));
  await screen.findByText("Undid Delete task “Pack”.");

  // Checklist conversion offers the AST items and sends the chosen ones.
  await user.click(row);
  await user.click(await screen.findByText(/Convert checklist to subtasks/));
  await user.click(screen.getByRole("checkbox", { name: "Charger" }));
  await user.click(screen.getByRole("button", { name: "Convert 1 to subtasks" }));
  await waitFor(() => assert.equal(converted.length, 1));
  assert.deepEqual(converted[0].items, [{ itemKey: "Passport#0", title: "Passport", done: false }]);
  cleanup();
});

test("the journal shows diary Markdown and history events, opens an earlier version and restores chosen values", async () => {
  const { TaskJournal } = await import("./TaskJournal");
  const restored: { fields: readonly string[]; entryId: string; expectedVersion: string }[] = [];
  const event = { id: "h1", operationId: "o1", eventKind: "task.updated", changedFields: ["Title", "Notes"], effectiveAtUtc: "2026-11-10T09:00:00.0000000Z", version: "2" };
  const journalApi = {
    diaryRpc: { ...diaryRpc,
      timeline: async () => ({ ok: true, error: null, items: [
        { atUtc: event.effectiveAtUtc, diary: null, event },
        { atUtc: "2026-11-10T10:00:00.0000000Z", event: null, diary: { id: "d1", taskId, entryAtUtc: "2026-11-10T10:00:00.0000000Z", markdown: "Called **twice**", revision: "1", editedAtUtc: null, version: "1", deleted: false } },
      ] }),
      list: async () => ({ ok: true, entries: [], error: null }),
    },
    historyRpc: { ...historyRpc,
      asOf: async () => ({ ok: true, existed: true, error: null, entry: event, snapshot: { task: task(taskId, "Old title", "old *notes*"), deleted: false, subtasks: [], tagIds: [] } }),
      restoreFields: async (request: { fields: readonly string[]; entryId: string; expectedVersion: string }) => { restored.push(request); return { ok: true, operationId: "r", task: task(taskId, "Old title"), error: null, warning: null }; },
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  let saved: TaskResponse | undefined;
  render(<TaskJournal task={task(taskId, "New title")} api={journalApi} revision={0} busy={false} onChanged={value => { saved = value; }} onError={message => assert.fail(message)}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  const timeline = await screen.findByRole("list", { name: "Timeline" });
  assert.equal(within(timeline).getByText("twice").tagName, "STRONG");
  assert.ok(within(timeline).getByText("Edited: title, notes"));
  await user.click(within(timeline).getByRole("button", { name: "View" }));
  const version = await screen.findByRole("region", { name: "Earlier version" });
  assert.ok(within(version).getByText("Old title"));
  assert.equal(within(version).getByText("notes").tagName, "EM");
  await user.click(within(version).getByRole("checkbox", { name: "Title" }));
  await user.click(within(version).getByRole("button", { name: "Restore selected" }));
  await waitFor(() => assert.equal(restored.length, 1));
  assert.deepEqual(restored[0].fields, ["Title"]);
  assert.equal(restored[0].entryId, "h1");
  assert.equal(restored[0].expectedVersion, "3");
  assert.equal(saved?.title, "Old title");
  cleanup();
});

test("a connected journal offers explicit GitHub posting and deletion choices", async () => {
  const { TaskJournal } = await import("./TaskJournal");
  const deleted: boolean[] = [];
  const entry = { id: "d1", taskId, entryAtUtc: "2026-11-10T10:00:00Z", markdown: "Comment", revision: "1", editedAtUtc: null, version: "1", deleted: false };
  const journalApi = { diaryRpc: { ...diaryRpc,
    timeline: async () => ({ ok: true, error: null, items: [{ atUtc: entry.entryAtUtc, event: null, diary: entry }] }),
    list: async () => ({ ok: true, entries: [], error: null }),
    delete: async (request: { deleteOnGitHub: boolean }) => { deleted.push(request.deleteOnGitHub); return { ok: true, entry, error: null }; },
  }, historyRpc };
  let statusReads = 0;
  const githubApi = { ...gitHubRpc, taskSync: async () => { statusReads++; return { ok: true, connected: true, pending: false, state: "Linked", error: null, assignedToMe: false, postDiaryByDefault: true, linkVersion: "1", hasConflict: false, syncError: null, issueUrl: null, repository: null, dueDateManaged: false, syncing: false }; } };
  const user = userEvent.setup({ document: dom.window.document });
  const view = render(<TaskJournal task={task(taskId, "Task")} api={journalApi} githubApi={githubApi} revision={0} busy={false} onChanged={() => {}} onError={message => assert.fail(message)}/>);
  try {
    assert.equal((await screen.findByRole("checkbox", { name: "Also post to GitHub on sync" }) as HTMLInputElement).checked, true);
    await user.click(screen.getByRole("checkbox", { name: "Also post to GitHub on sync" }));
    view.rerender(<TaskJournal task={task(taskId, "Task")} api={journalApi} githubApi={githubApi} revision={1} busy={false} onChanged={() => {}} onError={message => assert.fail(message)}/>);
    await waitFor(() => assert.equal(statusReads, 2));
    assert.equal((screen.getByRole("checkbox", { name: "Also post to GitHub on sync" }) as HTMLInputElement).checked, false);
    await user.click(await screen.findByRole("button", { name: "Delete diary entry" }));
    const dialog = screen.getByRole("dialog", { name: "Delete diary entry" });
    await user.click(within(dialog).getByRole("button", { name: "Delete on GitHub too" }));
    await waitFor(() => assert.deepEqual(deleted, [true]));
  } finally { cleanup(); }
});

test("the task editor opens on a roomy Notes tab, keeps fields on Details and saves changes by itself", async () => {
  const edits: { title: string; notes: string }[] = [];
  const item = task(taskId, "Write report", "Draft");
  const api = {
    taskRpc: { ...taskRpc,
      listInbox: async () => ({ ok: true, tasks: [item], error: null }),
      listDeleted: async () => ({ ok: true, tasks: [], error: null }),
      edit: async (request: { title: string; notes: string }) => { edits.push(request); return { ok: true, operationId: "e", task: { ...item, ...request }, error: null, warning: null }; },
    },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
    tagRpc: { ...tagRpc, list: async () => ({ ok: true, tags: [], error: null }), listDeleted: async () => ({ ok: true, tags: [], error: null }), targetTags: async () => ({ ok: true, tagIds: [], error: null }) },
    queryRpc: { ...queryRpc, counts: async () => ({ ok: true, views: [], tags: [], containers: [], error: null }) },
    undoRpc: { ...undoRpc, list: async () => ({ ok: true, entries: [], error: null }) },
    markdownRpc: { ...markdownRpc, resolveLinks: async () => ({ ok: true, targets: [], error: null }) },
    diaryRpc: { ...diaryRpc, timeline: async () => ({ ok: true, items: [], error: null }), list: async () => ({ ok: true, entries: [], error: null }) },
    historyRpc,
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" api={api} templates={<></>}/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  await user.click(await screen.findByRole("button", { name: "Write report" }));
  assert.equal(screen.getByRole("tab", { name: "Notes" }).getAttribute("aria-selected"), "true");
  const notes = dom.window.document.getElementById("task-notes") as HTMLTextAreaElement;
  assert.match(notes.className, /min-h-\[max\(16rem/);
  assert.equal(screen.queryByLabelText("Status"), null, "fields are not crowding the notes");
  await user.type(notes, " v2");

  await user.click(screen.getByRole("tab", { name: "Details" }));
  assert.ok(screen.getByLabelText("Status"));
  await user.click(screen.getByRole("tab", { name: /Subtasks/ }));
  assert.ok(screen.getByRole("heading", { name: "Subtasks" }));
  await waitFor(() => assert.equal(edits.at(-1)?.notes, "Draft v2"), { timeout: 3000 });
  assert.deepEqual({ title: edits.at(-1)!.title, notes: edits.at(-1)!.notes }, { title: "Write report", notes: "Draft v2" });
  assert.ok(await screen.findByText("All changes saved"));
  await user.click(screen.getByRole("button", { name: "Done" }));
  await waitFor(() => assert.equal(screen.queryByRole("tab", { name: "Notes" }), null));
  cleanup();
});

test("pad-note links resolve by ID, open the note, and show a deleted note as a broken link", async () => {
  const live = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
  const gone = "a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11";
  const source = `See ${padLinkMarkdown("Trip [ideas]", live)} and ${padLinkMarkdown("old note", gone)}.`;
  const opened: string[] = [];
  const asked: string[][] = [];
  render(<MarkdownPreview source={source} links={{
    resolvePads: async ids => { asked.push(ids); return [{ id: live, title: "Trip ideas", deleted: false }, { id: gone, title: "old note", deleted: true }]; },
    onOpenPad: id => opened.push(id),
  }}/>, { container: dom.window.document.body });

  await screen.findByText("(deleted note)");
  assert.deepEqual(asked[0].sort(), [live, gone].sort());
  assert.deepEqual(internalLinkIds(source), []); // pad links are not task links
  fireEvent.click(screen.getByRole("button", { name: "Trip [ideas]" }));
  assert.deepEqual(opened, [live]);
  cleanup();
});

test("the editor expands to fill the window, keeps the caret and text, and Esc brings it back", async () => {
  function Host() {
    const [value, setValue] = React.useState("First line\nSecond line");
    return <div data-testid="panel"><MarkdownEditor id="expand-notes" label="Notes" value={value} onChange={setValue}/></div>;
  }
  render(<Host/>, { container: dom.window.document.body.appendChild(dom.window.document.createElement("div")) });
  try {
    const area = () => dom.window.document.getElementById("expand-notes") as HTMLTextAreaElement;
    area().focus();
    area().setSelectionRange(5, 5);
    fireEvent.click(screen.getByRole("button", { name: "Expand editor" }));
    const expanded = await screen.findByRole("dialog", { name: "Notes (expanded)" });
    assert.equal(expanded.parentElement, dom.window.document.body, "drawn above every panel");
    assert.equal(within(screen.getByTestId("panel")).queryByRole("textbox"), null, "the panel keeps only a placeholder");
    await waitFor(() => assert.equal(dom.window.document.activeElement, area()));
    assert.equal(area().selectionStart, 5);
    fireEvent.change(area(), { target: { value: "First line\nSecond line\nThird" } });

    fireEvent.keyDown(dom.window as unknown as Window, { key: "Escape" });
    await waitFor(() => assert.equal(screen.queryByRole("dialog", { name: "Notes (expanded)" }), null));
    assert.ok(within(screen.getByTestId("panel")).getByRole("textbox"));
    assert.equal(area().value, "First line\nSecond line\nThird");
  } finally { cleanup(); }
});
