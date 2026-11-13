import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { gitHubRpc, type GitHubTaskSyncResponse } from "#neoastra";
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, { window: { value: dom.window, configurable: true }, document: { value: dom.window.document, configurable: true }, navigator: { value: dom.window.navigator, configurable: true }, HTMLElement: { value: dom.window.HTMLElement, configurable: true } });
const { render, screen, waitFor, cleanup, act, fireEvent } = await import("@testing-library/react");
const { GitHubTaskNotice } = await import("./GitHubTaskNotice");
const ordinary: GitHubTaskSyncResponse = { ok: true, connected: false, pending: false, state: null, error: null, assignedToMe: null, postDiaryByDefault: false, linkVersion: null, hasConflict: false, syncError: null, issueUrl: null, repository: null, dueDateManaged: false, syncing: false, assignees: null };

test("ordinary tasks show no GitHub mark and only query local task metadata", async () => {
  const requests: string[] = [];
  const api = { ...gitHubRpc, taskSync: async ({ taskId }: { taskId: string }) => { requests.push(taskId); return ordinary; } };
  render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try { await waitFor(() => assert.deepEqual(requests, ["t1"])); assert.equal(screen.queryByRole("status", { name: "GitHub sync status" }) === null, true); }
  finally { cleanup(); }
});

test("pending changes say not sent and reload after a saved version changes", async () => {
  let response: GitHubTaskSyncResponse = { ...ordinary, connected: true, pending: true, state: "Out of scope" };
  const api = { ...gitHubRpc, taskSync: async () => response };
  const view = render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try {
    await screen.findByText("Not sent to GitHub yet."); assert.ok(screen.getByText("GitHub · Out of scope"));
    response = { ...response, pending: false, state: "Linked" };
    view.rerender(<GitHubTaskNotice taskId="t1" version="2" revision={0} api={api}/>);
    await screen.findByText("GitHub · Linked"); assert.equal(screen.queryByText("Not sent to GitHub yet.") === null, true);
    assert.ok(screen.getByText(/Sync sends issue changes/));
  } finally { cleanup(); }
});

test("a late previous task response cannot mark the newly selected ordinary task", async () => {
  let finish!: (value: GitHubTaskSyncResponse) => void;
  const delayed = new Promise<GitHubTaskSyncResponse>(resolve => { finish = resolve; });
  const api = { ...gitHubRpc, taskSync: async ({ taskId }: { taskId: string }) => taskId === "old" ? delayed : ordinary };
  const view = render(<GitHubTaskNotice taskId="old" version="1" revision={0} api={api}/>);
  try {
    view.rerender(<GitHubTaskNotice taskId="new" version="1" revision={0} api={api}/>);
    await act(async () => { finish({ ...ordinary, connected: true, pending: true, state: "Linked" }); await delayed; });
    assert.equal(screen.queryByRole("status", { name: "GitHub sync status" }) === null, true);
  } finally { cleanup(); }
});

test("self-assignment queues a local change and updates the task notice", async () => {
  const requests: { taskId: string; assigned: boolean }[] = [];
  const api = { ...gitHubRpc, taskSync: async () => ({ ...ordinary, connected: true, state: "Linked", assignedToMe: requests.at(-1)?.assigned ?? false, pending: requests.length > 0, linkVersion: String(requests.length + 1) }),
    setSelfAssignment: async (request: { taskId: string; assigned: boolean }) => { requests.push(request); return { ok: true, error: null }; } };
  render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try {
    fireEvent.click(await screen.findByRole("button", { name: "Assign me" }));
    await screen.findByRole("button", { name: "Unassign me" });
    assert.equal(requests[0].taskId, "t1");
    assert.equal(requests[0].assigned, true);
    assert.ok(screen.getByText("Not sent to GitHub yet."));
  } finally { cleanup(); }
});

test("linked issues show their assignees and the new assignee after Assign me", async () => {
  let assignees = ["octocat"];
  const api = { ...gitHubRpc, taskSync: async () => ({ ...ordinary, connected: true, state: "Linked", assignedToMe: assignees.includes("person"), assignees }),
    setSelfAssignment: async () => { assignees = ["person", ...assignees]; return { ok: true, error: null }; } };
  render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try {
    await screen.findByText("Assigned to octocat");
    fireEvent.click(screen.getByRole("button", { name: "Assign me" }));
    await screen.findByText("Assigned to person, octocat");
  } finally { cleanup(); }
});

test("an issue with no assignees says Unassigned, and unknown assignees show nothing", async () => {
  let response: GitHubTaskSyncResponse = { ...ordinary, connected: true, state: "Linked", assignees: [] };
  const api = { ...gitHubRpc, taskSync: async () => response };
  const view = render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try {
    await screen.findByText("Unassigned");
    response = { ...response, assignees: null };
    view.rerender(<GitHubTaskNotice taskId="t1" version="2" revision={0} api={api}/>);
    await waitFor(() => assert.equal(screen.queryByText("Unassigned") === null, true));
    assert.equal(screen.queryByText(/Assigned to/) === null, true);
  } finally { cleanup(); }
});

test("conflict dismissal, retry and detach refresh versioned local metadata", async () => {
  let response: GitHubTaskSyncResponse = { ...ordinary, connected: true, state: "Needs attention", linkVersion: "7", hasConflict: true, syncError: "GitHub denied access." };
  const requests: { expectedVersion: string; taskId: string }[] = [];
  const api = { ...gitHubRpc, taskSync: async () => response,
    dismissConflict: async (request: { expectedVersion: string; taskId: string }) => { requests.push(request); response = { ...response, hasConflict: false, linkVersion: "8" }; return { ok: true, error: null }; },
    retryTask: async (request: { expectedVersion: string; taskId: string }) => { requests.push(request); response = { ...response, syncError: null, state: "Linked", linkVersion: "9" }; return { ok: true, error: null }; },
    keepAsOrdinaryTask: async (request: { expectedVersion: string; taskId: string }) => { requests.push(request); response = { ...response, connected: false }; return { ok: true, error: null }; } };
  render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try {
    fireEvent.click(await screen.findByRole("button", { name: "Dismiss conflict" }));
    await waitFor(() => assert.equal(screen.queryByRole("button", { name: "Dismiss conflict" }) === null, true));
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    await screen.findByText("Issue queued for background sync.");
    fireEvent.click(screen.getByRole("button", { name: "Keep as ordinary task" }));
    await waitFor(() => assert.equal(screen.queryByRole("status", { name: "GitHub sync status" }) === null, true));
    assert.deepEqual(requests.map(r => r.expectedVersion), ["7", "8", "9"]);
    assert.ok(requests.every(r => r.taskId === "t1"));
  } finally { cleanup(); }
});

test("a failed task action keeps the notice and displays its error", async () => {
  const api = { ...gitHubRpc, taskSync: async () => ({ ...ordinary, connected: true, state: "Linked", linkVersion: "3" }),
    keepAsOrdinaryTask: async () => { throw new Error("Reload this task and try again."); } };
  render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api}/>);
  try {
    fireEvent.click(await screen.findByRole("button", { name: "Keep as ordinary task" }));
    await screen.findByText("Reload this task and try again.");
    assert.equal(screen.getByRole("button", { name: "Keep as ordinary task" }).hasAttribute("disabled"), false);
  } finally { cleanup(); }
});

test("issue sync polls local status and refreshes the task when background work finishes", async () => {
  let response = { ...ordinary, connected: true, state: "Linked", syncing: true, pending: true };
  let refreshed = 0;
  const api = { ...gitHubRpc, taskSync: async () => response };
  render(<GitHubTaskNotice taskId="t1" version="1" revision={0} api={api} pollIntervalMs={10} onSynced={() => { refreshed++; }}/>);
  try {
    await screen.findByText("Syncing this issue…");
    response = { ...response, syncing: false, pending: false };
    await waitFor(() => assert.equal(refreshed, 1));
    assert.equal(screen.queryByText("Syncing this issue…") === null, true);
    assert.equal(screen.queryByText("Not sent to GitHub yet.") === null, true);
  } finally { cleanup(); }
});

test("an issue sync finishing before status is read still refreshes once", async () => {
  let response = { ...ordinary, connected: true, state: "Linked", linkVersion: "1" };
  let refreshed = 0, reads = 0;
  const api = { ...gitHubRpc, taskSync: async () => { reads++; return response; } };
  const props = { taskId: "t1", version: "1", api, onSynced: () => { refreshed++; } };
  const view = render(<GitHubTaskNotice {...props} revision={0}/>);
  try {
    await screen.findByRole("button", { name: "Keep as ordinary task" });
    response = { ...response, linkVersion: "2" };
    view.rerender(<GitHubTaskNotice {...props} revision={1}/>);
    await waitFor(() => assert.equal(refreshed, 1));
    view.rerender(<GitHubTaskNotice {...props} revision={2}/>);
    await waitFor(() => assert.equal(reads, 3));
    assert.equal(refreshed, 1);
  } finally { cleanup(); }
});
