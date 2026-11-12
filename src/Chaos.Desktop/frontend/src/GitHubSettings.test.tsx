import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { gitHubRpc, type GitHubStatusResponse, type GitHubAddRequest } from "#neoastra";
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true }, document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true }, HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { GitHubSettings } = await import("./GitHubSettings");
const disconnected: GitHubStatusResponse = { ok: true, connection: null, repositories: [], targets: [{ id: "inbox", name: "Inbox", sections: [] }], error: null };
const connected: GitHubStatusResponse = { ...disconnected, connection: { id: "c1", version: "1", accountId: "3000000000", accountLogin: "person", state: "Active", postDiaryByDefault: true, createLabelsForTags: false } };
const remote = { repositoryId: "4000000000", owner: "person", name: "repo", fullName: "person/repo", isPrivate: true, hasIssues: true, alreadyAdded: false };

test("opening GitHub settings reads local status without discovery or connecting", async () => {
  let connects = 0, discoveries = 0;
  const api = { ...gitHubRpc, status: async () => disconnected, connect: async () => { connects++; return { ok: true, error: null }; }, listRepositories: async () => { discoveries++; return { ok: true, repositories: [], nextCursor: null, error: null }; } };
  render(<GitHubSettings api={api}/>);
  try { await screen.findByRole("button", { name: "Connect using GitHub CLI" }); assert.equal(connects, 0); assert.equal(discoveries, 0); assert.match(screen.getByText(/gh auth login/).textContent!, /github.com/); }
  finally { cleanup(); }
});

test("Connect reports a setup error and can be retried", async () => {
  let status = disconnected, attempts = 0;
  const api = { ...gitHubRpc, status: async () => status, connect: async () => { if (++attempts === 1) return { ok: false, error: { kind: "githubCliUnavailable", message: "Install GitHub CLI first.", fields: [], correlationId: "id" } }; status = connected; return { ok: true, error: null }; } };
  render(<GitHubSettings api={api}/>); const user = userEvent.setup({ document: dom.window.document });
  try { await user.click(await screen.findByRole("button", { name: "Connect using GitHub CLI" })); await screen.findByText("Install GitHub CLI first."); await user.click(screen.getByRole("button", { name: "Connect using GitHub CLI" })); await screen.findByText("@person"); assert.equal(attempts, 2); }
  finally { cleanup(); }
});

test("browsing and adding a repository sends filters and string identity", async () => {
  const added: GitHubAddRequest[] = [];
  const api = { ...gitHubRpc, status: async () => connected, listRepositories: async () => ({ ok: true, repositories: [remote], nextCursor: null, error: null }), addRepository: async (request: GitHubAddRequest) => { added.push(request); return { ok: true, error: null }; } };
  render(<GitHubSettings api={api}/>); const user = userEvent.setup({ document: dom.window.document });
  try {
    await user.click(await screen.findByRole("button", { name: "Browse repositories" })); await user.click(await screen.findByRole("button", { name: "Choose" }));
    await user.type(screen.getByLabelText("Labels (comma separated)"), "bug, urgent");
    await user.selectOptions(screen.getByLabelText("Assigned to"), "me");
    await user.click(screen.getByRole("button", { name: "Save repository" }));
    await waitFor(() => assert.equal(added.length, 1));
    assert.equal(added[0].repositoryId, "4000000000"); assert.equal(added[0].targetContainerId, "inbox"); assert.deepEqual(added[0].filter.labels, ["bug", "urgent"]); assert.equal(added[0].filter.assignee, "me");
  } finally { cleanup(); }
});

test("disconnect requires confirmation and explains the preserved CLI login", async () => {
  let status = connected, calls = 0;
  const api = { ...gitHubRpc, status: async () => status, disconnect: async () => { calls++; status = disconnected; return { ok: true, error: null }; } };
  render(<GitHubSettings api={api}/>); const user = userEvent.setup({ document: dom.window.document });
  try {
    await user.click(await screen.findByRole("button", { name: "Disconnect" }));
    assert.equal(calls, 0); assert.match(screen.getByRole("alertdialog").textContent!, /CLI remains signed in/);
    await user.click(screen.getByRole("button", { name: "Cancel" })); assert.equal(calls, 0);
    await user.click(screen.getByRole("button", { name: "Disconnect" })); await user.click(screen.getByRole("button", { name: "Confirm disconnect" }));
    await screen.findByRole("button", { name: "Connect using GitHub CLI" }); assert.equal(calls, 1);
  } finally { cleanup(); }
});

test("a lost connection response retries the same operation ID", async () => {
  const operations: string[] = [];
  let status = disconnected;
  const api = { ...gitHubRpc, status: async () => status, connect: async (request: { operationId: string }) => { operations.push(request.operationId); if (operations.length === 1) throw new Error("Response lost. Retry the same change."); status = connected; return { ok: true, error: null }; } };
  render(<GitHubSettings api={api}/>); const user = userEvent.setup({ document: dom.window.document });
  try {
    await user.click(await screen.findByRole("button", { name: "Connect using GitHub CLI" }));
    await screen.findByText("Response lost. Retry the same change.");
    await user.click(screen.getByRole("button", { name: "Connect using GitHub CLI" }));
    await screen.findByText("@person");
    assert.equal(operations.length, 2); assert.equal(operations[0], operations[1]);
  } finally { cleanup(); }
});
