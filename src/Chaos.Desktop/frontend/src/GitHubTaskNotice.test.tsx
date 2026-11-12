import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { gitHubRpc, type GitHubTaskSyncResponse } from "#neoastra";
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, { window: { value: dom.window, configurable: true }, document: { value: dom.window.document, configurable: true }, navigator: { value: dom.window.navigator, configurable: true }, HTMLElement: { value: dom.window.HTMLElement, configurable: true } });
const { render, screen, waitFor, cleanup, act } = await import("@testing-library/react");
const { GitHubTaskNotice } = await import("./GitHubTaskNotice");
const ordinary: GitHubTaskSyncResponse = { ok: true, connected: false, pending: false, state: null, error: null };

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
    assert.ok(screen.getByText(/Only incoming changes/));
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
