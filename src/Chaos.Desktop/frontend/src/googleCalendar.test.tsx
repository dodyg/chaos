import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { CalendarSourceDto, GoogleAccountDto, GoogleCalendarChoiceDto } from "#neoastra";

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
const { GoogleCalendarSettings } = await import("./GoogleCalendarSettings");

afterEach(() => cleanup());

type Api = NonNullable<Parameters<typeof GoogleCalendarSettings>[0]["api"]>;
type SourceApi = NonNullable<Parameters<typeof GoogleCalendarSettings>[0]["sourceApi"]>;

const source = (patch: Partial<CalendarSourceDto> = {}): CalendarSourceDto => ({
  id: "g-team",
  provider: "Google",
  name: "Team",
  color: "#9a9cff",
  countsAsBusy: true,
  enabled: true,
  ownerEmail: null,
  version: "1",
  lastAttemptUtc: null,
  lastSuccessUtc: "2026-11-16T10:00:00Z",
  lastError: null,
  ...patch,
});
const account = (patch: Partial<GoogleAccountDto> = {}): GoogleAccountDto => ({
  id: "acct",
  email: "me@example.com",
  signedInHere: true,
  needsSignIn: false,
  error: null,
  lastSuccessUtc: "2026-11-16T10:00:00Z",
  calendars: [source()],
  ...patch,
});
const choice = (patch: Partial<GoogleCalendarChoiceDto> = {}): GoogleCalendarChoiceDto => ({
  calendarId: "team@example.com",
  name: "Team",
  color: "#9a9cff",
  primary: false,
  chosen: true,
  source: source(),
  ...patch,
});
const errorOf = (message: string) => ({ kind: "Validation", message, fields: [], correlationId: "c" });

function rig(
  accounts: GoogleAccountDto[],
  options: { configured?: boolean; choices?: GoogleCalendarChoiceDto[]; connect?: () => unknown } = {},
) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const api = {
    status: async () => ({ ok: true, configured: options.configured ?? true, accounts, error: null }),
    setup: async (request: Record<string, unknown>) => {
      calls.push({ method: "setup", request });
      return { ok: true, revoked: true, error: null };
    },
    connect: async (request: Record<string, unknown>) => {
      calls.push({ method: "connect", request });
      return options.connect ? options.connect() : { ok: true, account: account(), error: null };
    },
    calendars: async (request: Record<string, unknown>) => {
      calls.push({ method: "calendars", request });
      return {
        ok: true,
        calendars: options.choices ?? [
          choice(),
          choice({ calendarId: "me@example.com", name: "me@example.com", primary: true, chosen: false, source: null }),
        ],
        error: null,
      };
    },
    choose: async (request: Record<string, unknown>) => {
      calls.push({ method: "choose", request });
      return { ok: true, source: source({ version: "2", enabled: Boolean(request.chosen) }), refreshError: null, error: null };
    },
    disconnect: async (request: Record<string, unknown>) => {
      calls.push({ method: "disconnect", request });
      return { ok: true, revoked: true, error: null };
    },
  } as unknown as Api;
  const sourceApi = {
    update: async (request: Record<string, unknown>) => {
      calls.push({ method: "update", request });
      return { ok: true, source: source({ version: "2", countsAsBusy: Boolean(request.countsAsBusy) }), refreshError: null, error: null };
    },
    refresh: async (request: Record<string, unknown>) => {
      calls.push({ method: "refresh", request });
      return { ok: true, results: [], sources: [source({ lastSuccessUtc: "2026-11-16T11:00:00Z" })], error: null };
    },
  } as unknown as SourceApi;
  return { api, sourceApi, calls };
}

test("an account lists its chosen calendars with the last update and no sign-in button when signed in", async () => {
  const { api, sourceApi } = rig([account()]);
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  await screen.findByText("me@example.com");
  assert.ok(screen.getByLabelText("Colour of Team"));
  assert.ok(screen.getAllByText(/Last updated:/).length >= 1);
  assert.equal(screen.queryByRole("button", { name: /Sign in/ }), null);
  assert.ok(screen.getByRole("button", { name: "Connect another Google account" }));
});

test("an expired sign-in shows the plain message and a Sign in again button that connects", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, sourceApi, calls } = rig([account({ needsSignIn: true, error: "Google Calendar: sign in again." })]);
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  assert.match((await screen.findByRole("alert")).textContent ?? "", /Google Calendar: sign in again\./);
  assert.equal(screen.queryByRole("button", { name: "Choose calendars" }), null);
  await user.click(screen.getByRole("button", { name: "Sign in again" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "connect").length, 1));
  await waitFor(() => assert.equal(screen.queryByRole("button", { name: "Sign in again" }), null));
});

test("a device that knows the account only from sync offers Sign in on this device", async () => {
  const { api, sourceApi } = rig([account({ signedInHere: false, error: "Google Calendar: sign in on this device." })]);
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  assert.ok(await screen.findByRole("button", { name: "Sign in on this device" }));
  assert.match(screen.getByRole("alert").textContent ?? "", /sign in on this device/);
});

test("choosing a calendar sends the account, the calendar and the choice", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, sourceApi, calls } = rig([account()]);
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  await user.click(await screen.findByRole("button", { name: "Choose calendars" }));
  const group = await screen.findByRole("group", { name: "Show these calendars" });
  const main = within(group).getByLabelText(/me@example.com/) as HTMLInputElement;
  assert.equal(main.checked, false);
  await user.click(main);
  await waitFor(() => assert.ok(calls.some((call) => call.method === "choose")));
  const request = calls.find((call) => call.method === "choose")!.request;
  assert.equal(request.accountId, "acct");
  assert.equal(request.calendarId, "me@example.com");
  assert.equal(request.chosen, true);
  await waitFor(() => assert.equal((within(group).getByLabelText(/me@example.com/) as HTMLInputElement).checked, true));
});

test("busy time is saved with the source version, and refresh now asks for every calendar due or not", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, sourceApi, calls } = rig([account()]);
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  const row = await screen.findByLabelText("Colour of Team").then((el) => el.closest("li") as HTMLElement);
  await user.click(within(row).getByLabelText("Counts as busy time"));
  await waitFor(() => assert.ok(calls.some((call) => call.method === "update")));
  assert.equal(calls.find((call) => call.method === "update")!.request.expectedVersion, "1");
  assert.equal(calls.find((call) => call.method === "update")!.request.countsAsBusy, false);
  await user.click(within(row).getByRole("button", { name: "Refresh Team now" }));
  await waitFor(() => assert.ok(calls.some((call) => call.method === "refresh")));
  assert.equal(calls.find((call) => call.method === "refresh")!.request.onlyDue, false);
});

test("disconnect asks first, then removes the account from the list", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, sourceApi, calls } = rig([account()]);
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  await user.click(await screen.findByRole("button", { name: "Disconnect me@example.com" }));
  assert.equal(calls.filter((call) => call.method === "disconnect").length, 0);
  await screen.findByText(/Disconnect me@example.com on every device\?/);
  await user.click(screen.getByRole("button", { name: "Yes, disconnect" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "disconnect").length, 1));
  await waitFor(() => assert.equal(document.querySelectorAll("[data-google-account]").length, 0));
  assert.ok(screen.getByRole("button", { name: "Connect Google Calendar" }));
});

test("without a Google client, Settings asks for it once and then offers Connect", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, sourceApi, calls } = rig([], { configured: false });
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  await screen.findByRole("form", { name: "Set up Google sign-in" });
  assert.equal(screen.queryByRole("button", { name: /Connect/ }), null);
  await user.type(screen.getByLabelText("Client ID"), "123-abc.apps.googleusercontent.com");
  await user.type(screen.getByLabelText("Client secret"), "GOCSPX-secret");
  await user.click(screen.getByRole("button", { name: "Save and continue" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "setup").length, 1));
  assert.equal(calls.find((call) => call.method === "setup")!.request.clientId, "123-abc.apps.googleusercontent.com");
  await screen.findByRole("button", { name: "Connect Google Calendar" });
  assert.equal(document.body.textContent?.includes("GOCSPX-secret"), false);
});

test("a failed sign-in shows the message and adds no account", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, sourceApi } = rig([], { connect: () => ({ ok: false, account: null, error: errorOf("Google sign-in was cancelled.") }) });
  render(<GoogleCalendarSettings api={api} sourceApi={sourceApi} />);
  await user.click(await screen.findByRole("button", { name: "Connect Google Calendar" }));
  await screen.findByText(/Google sign-in was cancelled\./);
  assert.equal(document.querySelectorAll("[data-google-account]").length, 0);
});
