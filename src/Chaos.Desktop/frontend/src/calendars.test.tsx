import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { CalendarMeetingDto, CalendarRangeResponse, CalendarSourceDto, TaskResponse } from "#neoastra";

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
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { CalendarSettings } = await import("./CalendarSettings");
const { Calendar, localDate, toMeetingEvent, readableTextOn } = await import("./Calendar");
const { MeetingsToday, MeetingTaskNotice } = await import("./meetings");

afterEach(() => cleanup());
const today = localDate(new Date());

const source = (patch: Partial<CalendarSourceDto> = {}): CalendarSourceDto => ({
  id: "cal-1",
  provider: "Feed",
  name: "Team",
  color: "#3b82f6",
  countsAsBusy: true,
  enabled: true,
  ownerEmail: null,
  version: "1",
  lastAttemptUtc: null,
  lastSuccessUtc: "2026-11-16T10:00:00Z",
  lastError: null,
  ...patch,
});
const meeting = (patch: Partial<CalendarMeetingDto> = {}): CalendarMeetingDto => ({
  id: "cal-1|m1|d",
  sourceId: "cal-1",
  eventId: "m1",
  sourceName: "Team",
  title: "Design review",
  date: today,
  start: "14:00",
  end: "15:00",
  allDay: false,
  color: "#3b82f6",
  busy: true,
  location: "Room 2",
  videoUrl: null,
  webUrl: "https://example.com/m",
  ...patch,
});
const task = (id: string, title: string): TaskResponse => ({
  id,
  title,
  spaceId: "space",
  containerId: "inbox",
  notes: "",
  status: "NotStarted",
  plannedDate: today,
  dueDate: null,
  version: "1",
  createdAtUtc: "2026-01-01T00:00:00Z",
  updatedAtUtc: "2026-01-01T00:00:00Z",
  priority: "Normal",
  estimateMinutes: null,
  category: "Personal",
  plannedTime: null,
  plannedZoneId: null,
  dueTime: null,
  dueZoneId: null,
  sectionId: null,
  claimedById: null,
  claimedAtUtc: null,
  claimExpiresAtUtc: null,
  agentReady: false,
  agentAssigneeId: null,
});
const errorOf = (message: string, fieldPath: string) => ({
  kind: "Validation",
  message,
  fields: [{ fieldPath, message }],
  correlationId: "c",
});

function settingsApi(
  sources: CalendarSourceDto[],
  extra: (calls: { method: string; request: Record<string, unknown> }[]) => Record<string, unknown> = () => ({}),
) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const api = {
    list: async () => ({ ok: true, sources, error: null }),
    addFeed: async (request: Record<string, unknown>) => {
      calls.push({ method: "addFeed", request });
      return { ok: true, source: source({ id: "new", name: String(request.name) }), refreshError: null, error: null };
    },
    update: async (request: Record<string, unknown>) => {
      calls.push({ method: "update", request });
      return { ok: true, source: source({ version: "2", countsAsBusy: Boolean(request.countsAsBusy) }), refreshError: null, error: null };
    },
    remove: async (request: Record<string, unknown>) => {
      calls.push({ method: "remove", request });
      return { ok: true, error: null };
    },
    refresh: async () => ({ ok: true, results: [], sources, error: null }),
    ...extra(calls),
  } as unknown as NonNullable<Parameters<typeof CalendarSettings>[0]["api"]>;
  return { api, calls };
}

test("Settings → Calendars lists a calendar with its last update and plain error", async () => {
  const { api } = settingsApi([source({ lastError: "The calendar refused access." })]);
  render(<CalendarSettings api={api} />);
  await screen.findByLabelText("Name of Team");
  assert.match((await screen.findByRole("alert")).textContent ?? "", /refused access/);
  assert.ok(screen.getByText(/Last updated:/));
  assert.equal((screen.getByLabelText("Colour of Team") as HTMLInputElement).value, "#3b82f6");
});

test("a plain http address is refused and the backend message is shown on the address", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = settingsApi([], (list) => ({
    addFeed: async (request: Record<string, unknown>) => {
      list.push({ method: "addFeed", request });
      return {
        ok: false,
        source: null,
        refreshError: null,
        error: errorOf("Calendar feeds must use https://. Plain http:// is not allowed.", "address"),
      };
    },
  }));
  render(<CalendarSettings api={api} />);
  await user.type(await screen.findByLabelText(/Calendar address/), "http://example.com/a.ics");
  await user.type(screen.getByLabelText("Name"), "Team");
  await user.click(screen.getByRole("button", { name: "Add calendar" }));
  await waitFor(() => assert.ok(screen.getAllByText(/Plain http:\/\/ is not allowed/).length >= 1));
  assert.equal(calls.length, 1);
  assert.equal(document.querySelectorAll("[data-calendar-source]").length, 0);
});

test("an added feed appears, the address is cleared from the form, and busy is saved with the version", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = settingsApi([source()]);
  render(<CalendarSettings api={api} />);
  const address = (await screen.findByLabelText(/Calendar address/)) as HTMLInputElement;
  await user.type(address, "webcal://example.com/secret.ics");
  await user.type(screen.getByLabelText("Name"), "Holidays");
  await user.click(screen.getByRole("button", { name: "Add calendar" }));
  await screen.findByLabelText("Name of Holidays");
  assert.equal(address.value, "");
  assert.equal(document.body.textContent?.includes("secret.ics"), false);
  await user.click(within(document.querySelector('[data-calendar-source="cal-1"]') as HTMLElement).getByLabelText("Counts as busy time"));
  await waitFor(() => assert.ok(calls.some((call) => call.method === "update")));
  const update = calls.find((call) => call.method === "update")!.request;
  assert.equal(update.expectedVersion, "1");
  assert.equal(update.countsAsBusy, false);
});

test("removing a calendar asks first, then removes it", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, calls } = settingsApi([source()]);
  render(<CalendarSettings api={api} />);
  await user.click(await screen.findByRole("button", { name: "Remove Team" }));
  assert.equal(calls.length, 0);
  await screen.findByText(/Remove “Team” from every device\?/);
  await user.click(screen.getByRole("button", { name: "Yes, remove" }));
  await waitFor(() => assert.equal(calls.filter((call) => call.method === "remove").length, 1));
  await waitFor(() => assert.equal(document.querySelectorAll("[data-calendar-source]").length, 0));
});

test("a meeting is a read-only calendar event in its calendar colour, lighter when free", () => {
  const busy = toMeetingEvent(meeting());
  assert.equal(busy.editable, false);
  assert.equal(busy.start, `${today}T14:00:00`);
  assert.equal(busy.backgroundColor, "#3b82f6");
  assert.ok(!busy.classNames.includes("chaos-event-free"));
  const free = toMeetingEvent(meeting({ busy: false, allDay: true, start: null, end: null }));
  assert.equal(free.allDay, true);
  assert.ok(free.classNames.includes("chaos-event-free"));
});

test("a meeting on the calendar offers Make a task, which calls the RPC and offers the new task", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const opened: string[] = [];
  const range = async (request: Record<string, unknown>): Promise<CalendarRangeResponse> => ({
    ok: true,
    error: null,
    from: request.from as string,
    to: request.to as string,
    today,
    zoneId: "UTC",
    entries: [],
    meetings: [meeting()],
    days: [
      {
        date: today,
        availableMinutes: 360,
        workAllowanceMinutes: null,
        busyMinutes: 120,
        plannedMinutes: 0,
        workMinutes: 0,
        unknownEstimates: 0,
        openCount: 0,
        overTotal: false,
        overWork: false,
      },
    ],
    truncated: false,
  });
  const api = {
    range,
    unscheduled: async () => ({ ok: true, error: null, rows: [], totalCount: 0, nextCursor: null }),
  } as unknown as NonNullable<Parameters<typeof Calendar>[0]["api"]>;
  const calendarsApi = {
    makeTask: async (request: Record<string, unknown>) => {
      calls.push({ method: "makeTask", request });
      return { ok: true, task: task("t1", "Prepare: Design review"), replayed: false, error: null };
    },
  } as unknown as NonNullable<Parameters<typeof Calendar>[0]["calendarsApi"]>;
  const externals: string[] = [];
  render(
    <Calendar
      tags={[]}
      containers={[]}
      focus={null}
      busy={false}
      revision={0}
      today={today}
      onOpen={(item) => opened.push(item.id)}
      onChanged={() => {}}
      api={api}
      calendarsApi={calendarsApi}
      openExternal={(url) => externals.push(url)}
    />,
    { container: dom.window.document.body },
  );
  await user.click(await screen.findByText("Design review"));
  const menu = await screen.findByRole("region", { name: "Selected meeting" });
  await user.click(within(menu).getByRole("button", { name: "Open in browser" }));
  assert.deepEqual(externals, ["https://example.com/m"]);
  await user.click(within(menu).getByRole("button", { name: "Make a task" }));
  await waitFor(() => assert.equal(calls.length, 1));
  assert.equal(calls[0].request.sourceId, "cal-1");
  assert.equal(calls[0].request.eventId, "m1");
  assert.ok(String(calls[0].request.operationId).length > 10);
  await screen.findByText(/Made “Prepare: Design review”/);
  await user.click(screen.getByRole("button", { name: "Open task" }));
  assert.deepEqual(opened, ["t1"]);
  await waitFor(() => assert.ok(document.querySelector(`[data-busy="${today}"]`)));
  assert.match(document.querySelector(`[data-busy="${today}"]`)!.textContent ?? "", /2 h meetings/);
});

test("My Day lists the meetings, all-day first, and warns plainly when the tasks do not fit", () => {
  const shown = [
    meeting({ id: "b", title: "Design review" }),
    meeting({ id: "a", title: "Holiday", allDay: true, start: null, end: null, busy: false }),
  ];
  render(<MeetingsToday meetings={shown} busyMinutes={60} freeMinutes={120} doNotFit remainingMinutes={300} />);
  const items = within(screen.getByRole("region", { name: "Meetings today" })).getAllByRole("listitem");
  assert.match(items[0].textContent ?? "", /Holiday/);
  assert.match(items[1].textContent ?? "", /Design review/);
  assert.match(screen.getByRole("status").textContent ?? "", /Open tasks need 5 h but only 2 h is free after meetings/);
  cleanup();
  render(<MeetingsToday meetings={shown} busyMinutes={60} freeMinutes={420} doNotFit={false} remainingMinutes={30} />);
  assert.equal(screen.queryByRole("status"), null);
});

test("a task made from a meeting says which meeting it came from", async () => {
  const opened: string[] = [];
  const api = {
    taskLinks: async () => ({
      ok: true,
      error: null,
      links: [{ taskId: "t1", eventTitle: "Design review", eventStartUtc: "2026-11-17T14:00:00Z", eventWebUrl: "https://example.com/m" }],
    }),
  } as unknown as NonNullable<Parameters<typeof MeetingTaskNotice>[0]["api"]>;
  const user = userEvent.setup({ document: dom.window.document });
  render(<MeetingTaskNotice taskId="t1" api={api} onOpenLink={(url) => opened.push(url)} />);
  await screen.findByText(/From meeting Design review,/);
  await user.click(screen.getByRole("button", { name: "Open meeting" }));
  assert.deepEqual(opened, ["https://example.com/m"]);
});

test("meeting text is dark on light calendar colours and white on dark ones, so no theme loses it", () => {
  assert.equal(readableTextOn("#ffffff"), "#111111");
  assert.equal(readableTextOn("#fad165"), "#111111");
  assert.equal(readableTextOn("#9a9cff"), "#111111");
  assert.equal(readableTextOn("#16a765"), "#111111");
  assert.equal(readableTextOn("#3b82f6"), "#111111"); // black has the higher contrast (5.7 against 3.7 for white)
  assert.equal(readableTextOn("#1d4ed8"), "#ffffff");
  assert.equal(readableTextOn("#7b1fa2"), "#ffffff");
  assert.equal(readableTextOn("#000000"), "#ffffff");
  assert.equal(readableTextOn(null), "#ffffff");
  assert.equal(toMeetingEvent(meeting({ color: "#fad165" })).textColor, "#111111");
});
