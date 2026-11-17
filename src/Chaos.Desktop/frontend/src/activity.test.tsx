import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { gitHubRpc, reminderRpc, summaryRpc, viewRpc } from "#neoastra";
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost", pretendToBeVisual: true });
Object.defineProperties(globalThis, { window: { value: dom.window, configurable: true }, document: { value: dom.window.document, configurable: true }, navigator: { value: dom.window.navigator, configurable: true }, HTMLElement: { value: dom.window.HTMLElement, configurable: true } });
const { render, screen, waitFor, cleanup, act } = await import("@testing-library/react");
const { fakeActivity } = await import("./activityTestApi");
const { configureActivity, useActivity, useJobs } = await import("./activity");

/** A view that reloads when entries touching its kinds arrive; counts its loads. */
function Probe({ name, kinds, loads }: { name: string; kinds: string[]; loads: Record<string, number> }) {
  useActivity(kinds, () => { loads[name] = (loads[name] ?? 0) + 1; });
  return <p>{name}</p>;
}

function JobProbe() {
  const jobs = useJobs();
  return <p data-testid="job">{jobs["github.sync"]?.running ? "running" : "idle"}</p>;
}

async function ready(feed: ReturnType<typeof fakeActivity>) { await waitFor(() => assert.equal(feed.subscribed, true)); await waitFor(() => assert.equal(feed.calls.head >= 1, true)); }

test("a pushed activity.committed reloads the view that shows the changed kind and not unrelated ones", async () => {
  const feed = fakeActivity(); configureActivity(feed.api);
  const loads: Record<string, number> = {};
  render(<><Probe name="tasks" kinds={["task", "subtask"]} loads={loads}/><Probe name="calendar" kinds={["calendar_source"]} loads={loads}/></>);
  try {
    await ready(feed);
    feed.push(["task"]); feed.emit();
    await waitFor(() => assert.equal(loads.tasks, 1));
    assert.equal(loads.calendar ?? 0, 0);
    feed.push(["calendar_source"]); feed.emit();
    await waitFor(() => assert.equal(loads.calendar, 1));
    assert.equal(loads.tasks, 1);
  } finally { cleanup(); configureActivity(); }
});

test("a reset reloads every subscriber", async () => {
  const feed = fakeActivity(); configureActivity(feed.api);
  const loads: Record<string, number> = {};
  render(<><Probe name="tasks" kinds={["task"]} loads={loads}/><Probe name="calendar" kinds={["calendar_source"]} loads={loads}/></>);
  try {
    await ready(feed);
    feed.loseThePosition(); feed.emit();
    await waitFor(() => { assert.equal(loads.tasks, 1); assert.equal(loads.calendar, 1); });
  } finally { cleanup(); configureActivity(); }
});

test("job state follows the pushed events without polling", async () => {
  const feed = fakeActivity(); configureActivity(feed.api);
  render(<JobProbe/>);
  try {
    await ready(feed);
    assert.equal(screen.getByTestId("job").textContent, "idle");
    feed.setJobs([{ job: "github.sync", running: true, lastRunId: "r1" }]); feed.emit();
    await waitFor(() => assert.equal(screen.getByTestId("job").textContent, "running"));
    feed.setJobs([{ job: "github.sync", running: false, lastRunId: "r1" }]); feed.emit();
    await waitFor(() => assert.equal(screen.getByTestId("job").textContent, "idle"));
  } finally { cleanup(); configureActivity(); }
});

test("when subscribing fails the window falls back to reloading on focus", async () => {
  const feed = fakeActivity({ subscribeFails: true }); configureActivity(feed.api);
  const loads: Record<string, number> = {};
  render(<Probe name="tasks" kinds={["task"]} loads={loads}/>);
  try {
    // Focus before the failure is known does nothing; afterwards it reloads.
    await waitFor(async () => { await act(async () => { window.dispatchEvent(new window.Event("focus")); }); assert.ok((loads.tasks ?? 0) >= 1); });
  } finally { cleanup(); configureActivity(); }
});

test("with the stream available, focus does not reload", async () => {
  const feed = fakeActivity(); configureActivity(feed.api);
  const loads: Record<string, number> = {};
  render(<Probe name="tasks" kinds={["task"]} loads={loads}/>);
  try {
    await ready(feed);
    await act(async () => { window.dispatchEvent(new window.Event("focus")); });
    assert.equal(loads.tasks ?? 0, 0);
  } finally { cleanup(); configureActivity(); }
});

test("the components that used to poll start no interval timers", async () => {
  const feed = fakeActivity(); configureActivity(feed.api);
  const started: unknown[] = [];
  const realSet = globalThis.setInterval;
  const patched = ((handler: TimerHandler, ...rest: unknown[]) => { started.push(handler); return (realSet as (...args: unknown[]) => unknown)(handler, ...rest); }) as unknown as typeof setInterval;
  globalThis.setInterval = patched; (window as unknown as { setInterval: typeof setInterval }).setInterval = patched;
  const { SyncLogo } = await import("./SyncLogo");
  const { AlertStack } = await import("./Reminders");
  const { SummaryPrompts } = await import("./Summaries");
  const { ViewAlertStack } = await import("./ViewAlerts");
  const { GitHubTaskNotice } = await import("./GitHubTaskNotice");
  const idle = { ok: true, connected: false, pending: false, state: null, error: null, assignedToMe: null, postDiaryByDefault: false, linkVersion: null, hasConflict: false, syncError: null, issueUrl: null, repository: null, dueDateManaged: false, syncing: true, assignees: null };
  const reminders = { ...reminderRpc, alerts: async () => ({ ok: true, items: [], error: null }) } as unknown as typeof reminderRpc;
  const summaries = { ...summaryRpc, prompts: async () => ({ ok: true, prompts: [], error: null }) } as unknown as typeof summaryRpc;
  const views = { alerts: async () => ({ ok: true, alerts: [], error: null }), acknowledgeAlert: async () => ({ ok: true }) } as unknown as Pick<typeof viewRpc, "alerts" | "acknowledgeAlert">;
  const github = { ...gitHubRpc, taskSync: async () => ({ ...idle, connected: true, state: "Linked" }) } as unknown as typeof gitHubRpc;
  render(<><SyncLogo/><AlertStack api={reminders} onOpen={() => undefined} onChanged={() => undefined}/><SummaryPrompts api={summaries} onOpen={() => undefined}/><ViewAlertStack api={views} onOpen={() => undefined}/><GitHubTaskNotice taskId="t1" version="1" revision={0} api={github}/></>);
  try {
    // No waitFor here: Testing Library itself polls with setInterval, which would be counted.
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 120)); });
    assert.ok(screen.getByText("Syncing this issue…"));
    assert.equal(feed.subscribed, true);
    assert.equal(started.length, 0);
  } finally { globalThis.setInterval = realSet; (window as unknown as { setInterval: typeof setInterval }).setInterval = realSet; cleanup(); configureActivity(); }
});
