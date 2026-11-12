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
const disconnected: GitHubStatusResponse = { ok: true, connection: null, sync: null, repositories: [], targets: [{ id: "inbox", name: "Inbox", sections: [] }], error: null };
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

const repository = { id: "r1", version: "1", repositoryId: "4000000000", fullName: "person/repo", state: "Active", targetContainerId: "inbox", targetSectionId: null, filter: { labels: [], labelMode: "any", assignee: "any", milestone: null, noMilestone: false, includeClosedDays: 0 }, milestonesAsProjects: false };
const idleSync = { running: false, startedAtUtc: null, finishedAtUtc: null, created: null, updated: null, commentChanges: null, conflicts: null, morePages: false, error: null };

test("Sync now returns immediately, polls status and refreshes views after completion", async () => {
  let sync = idleSync as NonNullable<GitHubStatusResponse["sync"]>;
  let starts = 0, refreshes = 0;
  const api = { ...gitHubRpc, status: async () => ({ ...connected, repositories: [repository], sync }), syncNow: async () => { starts++; sync = { ...idleSync, running: true, startedAtUtc: "2026-11-12T09:00:00Z" }; return { ok: true, sync, error: null }; } };
  render(<GitHubSettings api={api} pollIntervalMs={10} onSynced={() => { refreshes++; }}/>); const user = userEvent.setup({ document: dom.window.document });
  try {
    await user.click(await screen.findByRole("button", { name: "Sync now" }));
    await screen.findByText("Fetching GitHub issues and comments…");
    assert.equal(starts, 1);
    assert.equal((screen.getByRole("button", { name: "Disconnect" }) as HTMLButtonElement).disabled, true);
    sync = { ...idleSync, startedAtUtc: "2026-11-12T09:00:00Z", finishedAtUtc: "2026-11-12T09:00:01Z", created: 2, updated: 1, commentChanges: 3, conflicts: 0, morePages: true };
    await screen.findByText(/Imported 2 tasks; updated 1 task; 3 comment changes/);
    await screen.findByRole("button", { name: "Continue sync" });
    await waitFor(() => assert.equal(refreshes, 1));
    assert.match(screen.getByText(/Actions on linked tasks sync/).textContent!, /newest 100 comments/);
  } finally { cleanup(); }
});

test("repository Sync uses its subscription ID and surfaces an account error with a fix", async () => {
  const requests: string[] = [];
  let state: GitHubStatusResponse = { ...connected, repositories: [repository], sync: idleSync };
  const api = { ...gitHubRpc, status: async () => state, syncNow: async (request: { repositoryId: string | null }) => {
    requests.push(request.repositoryId!);
    state = { ...state, connection: { ...connected.connection!, state: "NeedsSignIn" }, sync: { ...idleSync, startedAtUtc: "2026-11-12T09:00:00Z", finishedAtUtc: "2026-11-12T09:00:01Z", error: { kind: "githubAccountMismatch", message: "Sign in to the connected account again.", fields: [], correlationId: "id" } } };
    return { ok: true, sync: state.sync, error: null };
  } };
  render(<GitHubSettings api={api}/>); const user = userEvent.setup({ document: dom.window.document });
  try {
    await user.click(await screen.findByRole("button", { name: "Sync person/repo" }));
    await screen.findByText(/Sign in to the connected account again/);
    assert.deepEqual(requests, ["r1"]);
    assert.equal((screen.getByRole("button", { name: "Sync now" }) as HTMLButtonElement).disabled, true);
    assert.ok(screen.getByRole("button", { name: "Verify login" }));
  } finally { cleanup(); }
});
