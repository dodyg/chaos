import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import { queryRpc, recurrenceRpc, taskRpc, type TaskResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, { window: { value: dom.window, configurable: true }, document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true }, HTMLElement: { value: dom.window.HTMLElement, configurable: true }, crypto: { value: webcrypto, configurable: true } });
const { render, screen, waitFor, cleanup } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { RecurrenceEditor } = await import("./RecurrenceEditor");
const { QueryView } = await import("./QueryView");
const task: TaskResponse = { id: "task", spaceId: "space", containerId: "inbox", title: "Repeat", notes: "", status: "NotStarted", plannedDate: "2026-11-10", dueDate: null, version: "1", createdAtUtc: "", updatedAtUtc: "", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null };

test("previews a weekly rule before saving it", async () => {
  const calls: string[] = [];
  const api: typeof recurrenceRpc = { ...recurrenceRpc, get: async () => ({ ok: true, rule: null, ended: false, occurrences: [], error: null }),
    preview: async request => { calls.push(request.rule.frequency); return { ok: true, slots: ["2026-11-10T00:00:00"], error: null }; },
    start: async request => { calls.push(request.rule.frequency); return { ok: true, operationId: request.operationId, task: { ...task, version: "2" }, error: null, warning: null }; } };
  const tasks: typeof taskRpc = { ...taskRpc, get: async () => ({ ok: true, task, error: null }) };
  let saved = "";
  const user = userEvent.setup({ document: dom.window.document });
  render(<RecurrenceEditor task={task} revision={0} busy={false} api={api} taskApi={tasks} onChanged={value => { saved = value.version; }} onError={message => { throw new Error(message); }}/>, { container: dom.window.document.body });
  await waitFor(() => assert.equal((screen.getByRole("button", { name: "Preview" }) as HTMLButtonElement).disabled, false));
  await user.selectOptions(screen.getByLabelText("Frequency"), "Week");
  await user.click(screen.getByLabelText("Friday"));
  await user.click(screen.getByRole("button", { name: "Preview" }));
  await waitFor(() => assert.ok(screen.getByText("2026-11-10 00:00:00")));
  assert.deepEqual(calls, ["Week"]);
  await user.click(screen.getByRole("button", { name: "Start repeating" }));
  await waitFor(() => assert.equal(saved, "2"));
  assert.deepEqual(calls, ["Week", "Week"]);
  cleanup();
});

test("does not change recurrence while task edits are unsaved", async () => {
  let started = false;
  let error = "";
  const api: typeof recurrenceRpc = { ...recurrenceRpc, get: async () => ({ ok: true, rule: null, ended: false, occurrences: [], error: null }),
    start: async request => { started = true; return { ok: true, operationId: request.operationId, task, error: null, warning: null }; } };
  const tasks: typeof taskRpc = { ...taskRpc, get: async () => ({ ok: true, task: { ...task, title: "Saved title" }, error: null }) };
  const user = userEvent.setup({ document: dom.window.document });
  render(<RecurrenceEditor task={task} revision={0} busy={false} api={api} taskApi={tasks} onChanged={() => {}} onError={message => { error = message; }}/>, { container: dom.window.document.body });
  await waitFor(() => assert.equal((screen.getByRole("button", { name: "Start repeating" }) as HTMLButtonElement).disabled, false));
  await user.click(screen.getByRole("button", { name: "Start repeating" }));
  await waitFor(() => assert.match(error, /Save your task edits/));
  assert.equal(started, false);
  cleanup();
});

test("upcoming projections are labeled and cannot be opened or dragged", async () => {
  const api: typeof recurrenceRpc = { ...recurrenceRpc, upcoming: async () => ({ ok: true, items: [{ sourceTaskId: "task", title: "Repeat", nominalLocal: "2026-11-12T00:00:00" }], error: null }) };
  const queries: typeof queryRpc = { ...queryRpc, run: async () => ({ ok: true, rows: [], totalCount: 0, nextCursor: null, error: null }) };
  render(<QueryView query={{ view: "Upcoming" }} tags={[]} revision={0} selectedTaskId={null} busy={false} api={queries} recurrenceApi={api} onOpen={() => { throw new Error("Projection was opened"); }} header={null}/>, { container: dom.window.document.body });
  await waitFor(() => assert.ok(screen.getByText("Projected")));
  assert.equal(screen.queryByRole("button", { name: "Repeat" }), null);
  assert.equal(screen.getByText(/Repeat · 2026-11-12/).getAttribute("draggable"), null);
  cleanup();
});
