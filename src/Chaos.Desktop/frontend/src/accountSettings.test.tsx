import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { AccountInfoDto } from "#neoastra";

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
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { AccountSettings, formatBytes } = await import("./AccountSettings");

afterEach(() => cleanup());

type Api = NonNullable<Parameters<typeof AccountSettings>[0]["api"]>;

const account = (): AccountInfoDto => ({
  email: "me@example.com", displayName: null, storageBytes: 3 * 1024 * 1024, storageQuotaBytes: 5 * 1024 ** 3,
  devices: [
    { id: "d1", name: "Desk", platform: "windows", clientVersion: "0.2.1", lastSeenUtc: "2026-11-18T10:00:00Z", current: true },
    { id: "d2", name: "Laptop", platform: "windows", clientVersion: "0.2.1", lastSeenUtc: "2026-11-17T10:00:00Z", current: false },
  ],
});
const errorOf = (kind: string, message: string) => ({ kind, message });
const signedOutStatus = { ok: true, configured: true, signedIn: false, email: null, serviceHost: null, error: null };

function rig(options: { configured?: boolean; signedIn?: boolean; signIn?: () => unknown; account?: () => unknown; revoke?: (id: string) => unknown } = {}) {
  const calls: { method: string; request: unknown }[] = [];
  const api = {
    status: async () => ({ ok: true, configured: options.configured ?? true, signedIn: options.signedIn ?? false, email: options.signedIn ? "me@example.com" : null, serviceHost: "sync.example.com", error: null }),
    account: async () => options.account ? options.account() : { ok: true, account: account(), error: null },
    signIn: async (request: unknown) => { calls.push({ method: "signIn", request }); return options.signIn ? options.signIn() : { ok: true, account: account(), error: null }; },
    cancelSignIn: async (request: unknown) => { calls.push({ method: "cancelSignIn", request }); return signedOutStatus; },
    signOut: async (request: unknown) => { calls.push({ method: "signOut", request }); return signedOutStatus; },
    revokeDevice: async (request: { deviceId: string }) => { calls.push({ method: "revokeDevice", request }); return options.revoke ? options.revoke(request.deviceId) : { ok: true, account: { ...account(), devices: account().devices.filter(d => d.id !== request.deviceId) }, error: null }; },
  } as unknown as Api;
  return { api, calls };
}

test("signed out: offers the account with its reason and no device list", async () => {
  const { api } = rig();
  render(<AccountSettings api={api}/>);
  assert.ok(await screen.findByRole("button", { name: /Create an account or sign in/ }));
  assert.ok(screen.getByText(/Reason: sync with your other devices/i));
  assert.equal(screen.queryByText("Devices"), null);
});

test("not configured: explains instead of offering sign-in", async () => {
  const { api } = rig({ configured: false });
  render(<AccountSettings api={api}/>);
  await screen.findByText(/not set up in this build/);
  assert.equal(screen.queryByRole("button", { name: /Create an account or sign in/ }), null);
});

test("signing in waits for the browser, then shows the account and its devices", async () => {
  let finish: (value: unknown) => void = () => undefined;
  const { api, calls } = rig({ signIn: () => new Promise(resolve => { finish = resolve; }) });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Create an account or sign in/ }));
  await screen.findByText(/Finish signing in in your web browser/);
  assert.equal(calls.filter(call => call.method === "signIn").length, 1);
  finish({ ok: true, account: account(), error: null });
  await screen.findByText("me@example.com");
  assert.ok(screen.getByText("This computer"));
  assert.ok(screen.getByText(/3 MB of 5 GB used/));
  assert.equal(screen.getAllByRole("listitem").length, 2);
});

test("cancelling a sign-in asks the host to stop waiting", async () => {
  const { api, calls } = rig({ signIn: () => new Promise(() => undefined) });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Create an account or sign in/ }));
  await user.click(await screen.findByRole("button", { name: "Cancel" }));
  assert.ok(calls.some(call => call.method === "cancelSignIn"));
});

test("a failed sign-in shows the reason and keeps the button", async () => {
  const { api } = rig({ signIn: () => ({ ok: false, account: null, error: errorOf("Other", "Sign-in was cancelled or refused.") }) });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Create an account or sign in/ }));
  await screen.findByText("Sign-in was cancelled or refused.");
  assert.ok(screen.getByRole("button", { name: /Create an account or sign in/ }));
});

test("signed in: removing a device asks first, then lists the rest", async () => {
  const { api, calls } = rig({ signedIn: true });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await screen.findByText("Laptop");
  await user.click(screen.getByRole("button", { name: "Remove Laptop" }));
  assert.equal(calls.filter(call => call.method === "revokeDevice").length, 0);
  const confirm = screen.getByRole("group", { name: "Confirm removing Laptop" });
  await user.click(within(confirm).getByRole("button", { name: "Remove" }));
  await waitFor(() => assert.equal(screen.queryByText("Laptop"), null));
  assert.deepEqual(calls.filter(call => call.method === "revokeDevice").map(call => call.request), [{ deviceId: "d2" }]);
});

test("removing this computer signs out here", async () => {
  const { api } = rig({ signedIn: true, revoke: () => ({ ok: true, account: null, error: null }) });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await screen.findByText("Desk");
  await user.click(screen.getByRole("button", { name: "Remove Desk" }));
  await user.click(within(screen.getByRole("group", { name: "Confirm removing Desk" })).getByRole("button", { name: "Remove" }));
  await screen.findByRole("button", { name: /Create an account or sign in/ });
});

test("sign out returns to the signed-out view", async () => {
  const { api, calls } = rig({ signedIn: true });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Sign out/ }));
  await screen.findByRole("button", { name: /Create an account or sign in/ });
  assert.ok(calls.some(call => call.method === "signOut"));
});

test("an ended sign-in asks to sign in again", async () => {
  const { api } = rig({ signedIn: true, account: () => ({ ok: false, account: null, error: errorOf("SignInAgain", "Your sign-in to the online service ended. Sign in again.") }) });
  render(<AccountSettings api={api}/>);
  await screen.findByText(/Your sign-in to the online service ended/);
  await screen.findByRole("button", { name: "Sign in again" });
});

test("offline keeps the sign-in and offers to try again", async () => {
  const { api } = rig({ signedIn: true, account: () => ({ ok: false, account: null, error: errorOf("Offline", "The online service could not be reached.") }) });
  render(<AccountSettings api={api}/>);
  await screen.findByText("The online service could not be reached.");
  assert.ok(await screen.findByRole("button", { name: "Try again" }));
  assert.ok(screen.getByText(/me@example.com/));
});

test("sizes read in plain units", () => {
  assert.equal(formatBytes(0), "0 bytes");
  assert.equal(formatBytes(1536), "1.5 KB");
  assert.equal(formatBytes(5 * 1024 ** 3), "5 GB");
});
