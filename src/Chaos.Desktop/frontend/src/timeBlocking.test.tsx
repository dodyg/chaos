import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { TimeBlockStatusDto } from "#neoastra";

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
const { TimeBlockingSettings } = await import("./TimeBlockingSettings");

afterEach(() => cleanup());

type Api = NonNullable<Parameters<typeof TimeBlockingSettings>[0]["api"]>;

const status = (patch: Partial<TimeBlockStatusDto> = {}): TimeBlockStatusDto => ({
  enabled: false,
  accountId: null,
  accountEmail: null,
  showTitles: true,
  spaces: [
    { id: "personal", name: "Personal", personal: true, chosen: true },
    { id: "team", name: "Team", personal: false, chosen: true },
  ],
  calendarId: null,
  isRunner: false,
  lastPassUtc: null,
  lastError: null,
  needsSignIn: false,
  eventCount: 0,
  profileVersion: "7",
  accounts: [{ id: "acct", email: "me@example.com", signedInHere: true }],
  ...patch,
});
const enabled = (patch: Partial<TimeBlockStatusDto> = {}): TimeBlockStatusDto =>
  status({
    enabled: true,
    accountId: "acct",
    accountEmail: "me@example.com",
    calendarId: "calendar_1",
    isRunner: true,
    lastPassUtc: "2026-11-24T09:05:00Z",
    eventCount: 3,
    profileVersion: "8",
    ...patch,
  });

function rig(current: TimeBlockStatusDto, options: { configured?: boolean } = {}) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  let latest = current;
  const settle = (result: { status: TimeBlockStatusDto }) => ({ ok: true, error: null, ...result });
  const api = {
    status: async () => ({ ok: true, configured: options.configured ?? true, status: latest, error: null }),
    enable: async (request: Record<string, unknown>) => {
      calls.push({ method: "enable", request });
      latest = enabled({ showTitles: request.showTitles as boolean, profileVersion: "8" });
      return settle({ status: latest });
    },
    update: async (request: Record<string, unknown>) => {
      calls.push({ method: "update", request });
      latest = { ...latest, showTitles: request.showTitles as boolean, profileVersion: "9" };
      return settle({ status: latest });
    },
    runOnThisDevice: async (request: Record<string, unknown>) => {
      calls.push({ method: "runOnThisDevice", request });
      latest = { ...latest, isRunner: true, profileVersion: "10" };
      return settle({ status: latest });
    },
    signIn: async (request: Record<string, unknown>) => {
      calls.push({ method: "signIn", request });
      latest = { ...latest, needsSignIn: false, lastError: null };
      return settle({ status: latest });
    },
    syncNow: async (request: Record<string, unknown>) => {
      calls.push({ method: "syncNow", request });
      return {
        ok: true,
        error: null,
        pass: { ran: true, error: null, needsSignIn: false, pushed: 0, updated: 1, removed: 0, moved: 0, conflicts: 0 },
        status: latest,
      };
    },
    disable: async (request: Record<string, unknown>) => {
      calls.push({ method: "disable", request });
      latest = status({ ...latest, enabled: false, isRunner: false, eventCount: 0, profileVersion: "11" });
      return { ok: true, error: null, calendarDeleted: request.deleteCalendar, status: latest };
    },
  } as unknown as Api;
  return { api, calls };
}

test("off: the setup form offers Busy, a space choice and Turn on, which sends exactly that", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = rig(status());
  render(<TimeBlockingSettings api={api} />);
  const form = (await screen.findByRole("button", { name: "Turn on time blocking" })).closest("form") as HTMLElement;
  assert.equal(form?.dataset["timeBlock"], "off");
  assert.equal((within(form).getByLabelText(/Every space/) as HTMLInputElement).checked, true);
  await user.click(within(form).getByRole("radio", { name: /Show as Busy/ }));
  await user.click(within(form).getByLabelText(/Every space/));
  // With every space chosen, both are ticked; leaving only Team means unticking Personal.
  await user.click(within(form).getByLabelText(/Personal/));
  assert.equal((within(form).getByLabelText(/Team/) as HTMLInputElement).checked, true);
  await user.click(within(form).getByRole("button", { name: "Turn on time blocking" }));
  await waitFor(() => assert.equal(calls.length, 1));
  assert.equal(calls[0].method, "enable");
  assert.deepEqual(calls[0].request, { accountId: "acct", showTitles: false, spaceIds: ["team"] });
  await screen.findByText(/me@example\.com/);
  assert.match(screen.getByText(/on this device/).textContent ?? "", /on this device/);
  assert.equal(screen.queryByRole("button", { name: "Turn on time blocking" }), null);
});

test("on: Sync now asks for a pass, and turning off offers deleting the calendar, checked by default", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = rig(enabled());
  render(<TimeBlockingSettings api={api} />);
  await screen.findByText(/3 blocks/);
  await user.click(screen.getByRole("button", { name: "Sync now" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "syncNow").length, 1));
  await screen.findByText(/Synced with Google\./);

  await user.click(screen.getByRole("button", { name: "Turn off…" }));
  const confirm = await screen.findByRole("form", { name: "Turn off time blocking" });
  assert.equal((within(confirm).getByLabelText(/Delete the Chaos calendar in Google/) as HTMLInputElement).checked, true);
  await user.click(within(confirm).getByRole("button", { name: "Turn off" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "disable").length, 1));
  assert.deepEqual(calls.find((call) => call.method === "disable")!.request, { expectedVersion: "8", deleteCalendar: true });
  await waitFor(() => assert.ok(document.querySelector("[data-time-block='off']")));
});

test("a device that is not the runner offers Run on this device; one that must sign in again offers that", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = rig(enabled({ isRunner: false, needsSignIn: true, lastError: "Google Calendar: sign in again." }));
  render(<TimeBlockingSettings api={api} />);
  await screen.findByText(/on another device/);
  await user.click(screen.getByRole("button", { name: "Sign in again" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "signIn").length, 1));
  await waitFor(() => assert.equal(screen.queryByRole("button", { name: "Sign in again" }), null));
  await user.click(screen.getByRole("button", { name: "Run on this device" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "runOnThisDevice").length, 1));
  assert.equal(calls.find((call) => call.method === "runOnThisDevice")!.request.expectedVersion, "8");
  await screen.findByText(/on this device/);
});

test("titles and spaces are saved with the profile version; no Google account means no form", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = rig(enabled());
  render(<TimeBlockingSettings api={api} />);
  const titles = (await screen.findByLabelText("Show task titles")) as HTMLInputElement;
  await user.click(titles);
  await waitFor(() => assert.ok(calls.some((call) => call.method === "update")));
  assert.deepEqual(calls.find((call) => call.method === "update")!.request, { expectedVersion: "8", showTitles: false, spaceIds: null });
  cleanup();

  const none = rig(status({ accounts: [] }));
  render(<TimeBlockingSettings api={none.api} />);
  await screen.findByText(/Connect a Google account above/);
  assert.equal(document.querySelector("[data-time-block='off']"), null);
});
