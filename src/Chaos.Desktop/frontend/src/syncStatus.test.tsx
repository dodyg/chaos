import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { SyncStatusResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  Element: { value: dom.window.Element, configurable: true },
  Node: { value: dom.window.Node, configurable: true },
  DOMRect: { value: dom.window.DOMRect, configurable: true },
  CustomEvent: { value: dom.window.CustomEvent, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { SyncIndicator, SyncPanel, statusLabel, ago } = await import("./SyncStatus");

afterEach(() => cleanup());

type Api = NonNullable<Parameters<typeof SyncPanel>[0]["api"]>;

const status = (patch: Partial<SyncStatusResponse>): SyncStatusResponse => ({
  ok: true,
  kind: "Synced",
  lastSyncedUtc: new Date().toISOString(),
  pending: 0,
  detail: null,
  attention: [],
  ...patch,
});
const item = (id: string, actions = ["Sync now", "Dismiss"]) => ({
  id,
  category: "sync.parked",
  message: "Some changes could not be applied.",
  suggestion: "Syncing again may fix it.",
  createdUtc: "2026-11-18T10:00:00Z",
  actions,
});

function fake(initial: SyncStatusResponse) {
  const state = { current: initial, syncNow: 0, dismissed: [] as string[] };
  const api: Api = {
    status: async () => state.current,
    syncNow: async () => {
      state.syncNow++;
      return state.current;
    },
    dismiss: async ({ id }) => {
      state.dismissed.push(id);
      state.current = { ...state.current, kind: "Synced", attention: state.current.attention.filter((a) => a.id !== id) };
      return state.current;
    },
  };
  return { api, state };
}

test("the indicator shows nothing for a computer that never used an account", async () => {
  const { api } = fake(status({ kind: "Hidden" }));
  const { container } = render(<SyncIndicator api={api} onOpen={() => {}} />);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(container.querySelector("button"), null);
});

test("every state has its own wording and the indicator opens Settings when clicked", async () => {
  const cases: [Partial<SyncStatusResponse>, string][] = [
    [{ kind: "Synced" }, "Synced just now"],
    [{ kind: "Syncing" }, "Syncing…"],
    [{ kind: "Offline", pending: 3 }, "Offline — 3 changes waiting"],
    [{ kind: "Offline", pending: 1 }, "Offline — 1 change waiting"],
    [{ kind: "NeedsAttention", attention: [item("a"), item("b")] }, "Needs attention (2)"],
    [{ kind: "SignedOut" }, "Signed out"],
    [{ kind: "UpdateNeeded" }, "Update needed"],
  ];
  for (const [patch, expected] of cases) {
    cleanup();
    let opened = 0;
    render(
      <SyncIndicator
        api={fake(status(patch)).api}
        onOpen={() => {
          opened++;
        }}
      />,
    );
    const label = await screen.findByText(expected);
    assert.equal(statusLabel(status(patch)), expected);
    await userEvent.click(label);
    assert.equal(opened, 1, expected);
  }
});

test("the indicator follows the engine as the status changes", async () => {
  const { api, state } = fake(status({ kind: "Syncing" }));
  render(<SyncIndicator api={api} onOpen={() => {}} />);
  await screen.findByText("Syncing…");
  state.current = status({ kind: "Offline", pending: 2 });
  await waitFor(() => assert.ok(screen.getByText("Offline — 2 changes waiting")), { timeout: 6000 });
});

test("the Sync page shows the pending count and Sync now asks for a cycle", async () => {
  const { api, state } = fake(status({ kind: "Offline", pending: 4, detail: "Offline. 4 changes are waiting." }));
  render(<SyncPanel api={api} />);
  await screen.findByText("Offline. 4 changes are waiting.");
  assert.ok(screen.getByText("4 changes"));
  await userEvent.click(screen.getByRole("button", { name: /Sync now/ }));
  assert.equal(state.syncNow, 1);
});

test("attention items show their explanation and actions; dismissing one removes it", async () => {
  const { api, state } = fake(status({ kind: "NeedsAttention", attention: [item("one"), item("two", ["Dismiss"])] }));
  render(<SyncPanel api={api} />);
  const list = await screen.findByRole("list", { name: "Needs attention" });
  assert.equal(list.querySelectorAll("li").length, 2);
  assert.ok(screen.getAllByText("Syncing again may fix it.").length >= 1);
  const first = list.querySelector("[data-attention=one]")!;
  assert.ok(first.querySelector("button"));
  const second = list.querySelector("[data-attention=two]")!;
  assert.equal(second.querySelectorAll("button").length, 1);
  await userEvent.click(second.querySelector("button")!);
  await waitFor(() => assert.equal(list.querySelectorAll("li").length, 1));
  assert.deepEqual(state.dismissed, ["two"]);
  await userEvent.click(first.querySelectorAll("button")[0]);
  assert.equal(state.syncNow, 1);
});

test("the Sync page is absent while signed out of an account that never synced, and says so after sign-out", async () => {
  const hidden = render(<SyncPanel api={fake(status({ kind: "Hidden" })).api} />);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(hidden.container.querySelector("section"), null);
  hidden.unmount();
  render(
    <SyncPanel api={fake(status({ kind: "SignedOut", detail: "Signed out. This computer keeps its data and works offline." })).api} />,
  );
  await screen.findByText("Signed out. This computer keeps its data and works offline.");
  assert.equal((screen.getByRole("button", { name: /Sync now/ }) as HTMLButtonElement).disabled, true);
});

test("ago reads naturally", () => {
  const now = Date.parse("2026-11-18T12:00:00Z");
  assert.equal(ago(null, now), "not yet");
  assert.equal(ago("2026-11-18T11:59:58Z", now), "just now");
  assert.equal(ago("2026-11-18T11:59:00Z", now), "a minute ago");
  assert.equal(ago("2026-11-18T11:55:00Z", now), "5 minutes ago");
  assert.equal(ago("2026-11-18T09:00:00Z", now), "3 hours ago");
});
