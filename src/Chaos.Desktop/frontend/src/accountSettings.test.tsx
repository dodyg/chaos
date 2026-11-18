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

const joinedPreview = { ok: true, case: "AlreadyJoined", local: { tasks: 0, lists: 0, tags: 0, pages: 0 }, account: null, defaultMode: "Merge", error: null };

function rig(options: { configured?: boolean; signedIn?: boolean; signIn?: () => unknown; account?: () => unknown; revoke?: (id: string) => unknown; setAddress?: (address: string) => unknown; preview?: unknown; deleteAccount?: (confirmation: string) => unknown } = {}) {
  const calls: { method: string; request: unknown }[] = [];
  let configured = options.configured ?? true;
  let joined = false;
  const api = {
    status: async () => ({ ok: true, configured, signedIn: options.signedIn ?? false, email: options.signedIn ? "me@example.com" : null, serviceHost: "sync.example.com", error: null }),
    account: async () => options.account ? options.account() : { ok: true, account: account(), error: null },
    signIn: async (request: unknown) => { calls.push({ method: "signIn", request }); return options.signIn ? options.signIn() : { ok: true, account: account(), error: null }; },
    cancelSignIn: async (request: unknown) => { calls.push({ method: "cancelSignIn", request }); return signedOutStatus; },
    leave: async (request: unknown) => { calls.push({ method: "leave", request }); return signedOutStatus; },
    joinPreview: async () => joined ? joinedPreview : options.preview ?? joinedPreview,
    join: async (request: unknown) => { joined = true; calls.push({ method: "join", request }); return { ok: true, case: "ChooseData", mode: (request as { mode: string }).mode, safetyBackup: "chaos-1.db", error: null }; },
    deleteAccount: async (request: { confirmation: string }) => { calls.push({ method: "deleteAccount", request }); return options.deleteAccount ? options.deleteAccount(request.confirmation) : signedOutStatus; },
    reauthenticate: async (request: unknown) => { calls.push({ method: "reauthenticate", request }); return { ok: true, account: account(), error: null }; },
    address: async () => ({ ok: true, address: configured ? "https://sync.example.com/" : null, source: configured ? "Setting" : "None", saved: configured ? "https://sync.example.com/" : null, error: null }),
    setAddress: async (request: { address: string }) => {
      calls.push({ method: "setAddress", request });
      const result = options.setAddress ? options.setAddress(request.address) : { ok: true, address: request.address, source: "Setting", saved: request.address, error: null };
      if ((result as { ok: boolean }).ok) configured = request.address !== "";
      return result;
    },
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
  await screen.findByText(/No online service is set up yet/);
  assert.equal(screen.queryByRole("button", { name: /Create an account or sign in/ }), null);
  assert.ok(screen.getByLabelText("Service address"));
});

test("not configured: typing an address saves it and offers sign-in", async () => {
  const { api, calls } = rig({ configured: false });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.type(await screen.findByLabelText("Service address"), "sync.example.com");
  await user.click(screen.getByRole("button", { name: "Save" }));
  assert.ok(await screen.findByRole("button", { name: /Create an account or sign in/ }));
  assert.deepEqual(calls.find(c => c.method === "setAddress")?.request, { address: "sync.example.com" });
});

test("a refused address shows the reason and keeps the form", async () => {
  const { api } = rig({ configured: false, setAddress: () => ({ ok: false, address: null, source: "None", saved: null, error: "Enter the service's web address." }) });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.type(await screen.findByLabelText("Service address"), "nonsense");
  await user.click(screen.getByRole("button", { name: "Save" }));
  assert.ok(await screen.findByText(/Enter the service's web address/));
  assert.ok(screen.getByLabelText("Service address"));
});

test("signed out with a service: the address can be changed or cleared", async () => {
  const { api, calls } = rig();
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await screen.findByRole("button", { name: /Create an account or sign in/ });
  await user.click(await screen.findByRole("button", { name: "Clear" }));
  await screen.findByText(/No online service is set up yet/);
  assert.deepEqual(calls.find(c => c.method === "setAddress")?.request, { address: "" });
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

test("sign out asks what happens to the data, then returns to the signed-out view", async () => {
  const { api, calls } = rig({ signedIn: true });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Sign out/ }));
  assert.equal(calls.filter(call => call.method === "leave").length, 0);
  await user.click(within(screen.getByRole("group", { name: "Sign out" })).getByRole("button", { name: "Keep data on this device" }));
  await screen.findByRole("button", { name: /Create an account or sign in/ });
  assert.deepEqual(calls.filter(call => call.method === "leave").map(call => call.request), [{ keepData: true }]);
});

test("sign out can remove the account's data from this device", async () => {
  const { api, calls } = rig({ signedIn: true });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Sign out/ }));
  await user.click(screen.getByRole("button", { name: "Remove the account's data from this device" }));
  await screen.findByRole("button", { name: /Create an account or sign in/ });
  assert.deepEqual(calls.filter(call => call.method === "leave").map(call => call.request), [{ keepData: false }]);
});

const local = { tasks: 4, lists: 2, tags: 3, pages: 1 };
const remote = { tasks: 40, lists: 5, tags: 9, pages: 7 };

test("the first device is offered to start syncing, with its own counts", async () => {
  const { api, calls } = rig({ signedIn: true, preview: { ...joinedPreview, case: "FirstDevice", local } });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  const panel = await screen.findByRole("group", { name: "Start syncing" });
  assert.ok(within(panel).getByText(/4 tasks, 2 lists, 3 tags, 1 pages/));
  await user.click(within(panel).getByRole("button", { name: "Start syncing" }));
  await waitFor(() => assert.equal(screen.queryByRole("group", { name: "Start syncing" }), null));
  assert.equal(calls.filter(call => call.method === "join").length, 1);
});

test("joining an account that has data shows both sides and defaults to merge", async () => {
  const { api, calls } = rig({ signedIn: true, preview: { ...joinedPreview, case: "ChooseData", local, account: remote, defaultMode: "Merge" } });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  const panel = await screen.findByRole("group", { name: "Join your account" });
  assert.ok(within(panel).getByText(/4 tasks, 2 lists, 3 tags, 1 pages/));
  assert.ok(within(panel).getByText(/40 tasks, 5 lists, 9 tags, 7 pages/));
  assert.equal((within(panel).getByRole("radio", { name: /Merge this computer's data/ }) as HTMLInputElement).checked, true);
  assert.ok(within(panel).getByText(/safety backup/i));
  await user.click(within(panel).getByRole("button", { name: "Merge and start syncing" }));
  await waitFor(() => assert.equal(screen.queryByRole("group", { name: "Join your account" }), null));
  assert.deepEqual(calls.filter(call => call.method === "join").map(call => call.request), [{ mode: "Merge" }]);
});

test("replacing asks to confirm before it removes this computer's data", async () => {
  const { api, calls } = rig({ signedIn: true, preview: { ...joinedPreview, case: "ChooseData", local, account: remote, defaultMode: "Merge" } });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  const panel = await screen.findByRole("group", { name: "Join your account" });
  await user.click(within(panel).getByRole("radio", { name: /Replace this computer's data/ }));
  await user.click(within(panel).getByRole("button", { name: "Replace and start syncing" }));
  assert.equal(calls.filter(call => call.method === "join").length, 0);
  await user.click(within(screen.getByRole("group", { name: "Confirm replacing" })).getByRole("button", { name: "Replace" }));
  await waitFor(() => assert.equal(screen.queryByRole("group", { name: "Join your account" }), null));
  assert.deepEqual(calls.filter(call => call.method === "join").map(call => call.request), [{ mode: "Replace" }]);
});

test("a computer with no data defaults to taking the account's, with no data-loss prompt", async () => {
  const { api, calls } = rig({ signedIn: true, preview: { ...joinedPreview, case: "ChooseData", local: { tasks: 0, lists: 0, tags: 0, pages: 0 }, account: remote, defaultMode: "Replace" } });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  const panel = await screen.findByRole("group", { name: "Join your account" });
  assert.equal((within(panel).getByRole("radio", { name: /Replace this computer's data/ }) as HTMLInputElement).checked, true);
  await user.click(within(panel).getByRole("button", { name: "Replace and start syncing" }));
  await waitFor(() => assert.equal(calls.filter(call => call.method === "join").length, 1));
});

test("deleting the account needs the typed phrase", async () => {
  const { api, calls } = rig({ signedIn: true });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Delete account/ }));
  const form = screen.getByRole("group", { name: "Delete account" });
  assert.ok(within(form).getByText(/data on this computer and your other devices stays/i));
  const submit = within(form).getByRole("button", { name: "Delete my account" }) as HTMLButtonElement;
  assert.equal(submit.disabled, true);
  await user.type(within(form).getByLabelText("Type DELETE to confirm"), "DELETE");
  assert.equal(submit.disabled, false);
  await user.click(submit);
  await screen.findByRole("button", { name: /Create an account or sign in/ });
  assert.deepEqual(calls.filter(call => call.method === "deleteAccount").map(call => call.request), [{ confirmation: "DELETE" }]);
});

test("a deletion that needs a fresh sign-in signs in again and then goes through", async () => {
  let attempts = 0;
  const { api, calls } = rig({
    signedIn: true,
    deleteAccount: () => ++attempts === 1
      ? { ok: false, configured: true, signedIn: true, email: null, serviceHost: null, error: errorOf("FreshSignInRequired", "Sign in again to confirm deleting your account.") }
      : signedOutStatus,
  });
  const user = userEvent.setup();
  render(<AccountSettings api={api}/>);
  await user.click(await screen.findByRole("button", { name: /Delete account/ }));
  await user.type(screen.getByLabelText("Type DELETE to confirm"), "DELETE");
  await user.click(screen.getByRole("button", { name: "Delete my account" }));
  await user.click(await screen.findByRole("button", { name: "Sign in again to confirm" }));
  await screen.findByRole("button", { name: /Create an account or sign in/ });
  assert.deepEqual(calls.map(call => call.method).filter(m => m === "deleteAccount" || m === "reauthenticate"), ["deleteAccount", "reauthenticate", "deleteAccount"]);
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
