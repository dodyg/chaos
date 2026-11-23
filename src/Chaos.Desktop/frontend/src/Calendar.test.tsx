import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { CalendarDayDto, CalendarEntryDto, CalendarRangeResponse, QueryRowResponse, TaskResponse } from "#neoastra";

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
const { Calendar, toEvent, addDays, dayBadge, localDate } = await import("./Calendar");
const { SpacesProvider } = await import("./Sharing");

afterEach(() => cleanup());

const today = localDate(new Date());

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id,
    title,
    spaceId: "space",
    containerId: "inbox",
    notes: "",
    status: "NotStarted",
    plannedDate: null,
    dueDate: null,
    version: "7",
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
    ...patch,
  };
}
function entry(kind: string, id: string, title: string, patch: Partial<CalendarEntryDto> = {}): CalendarEntryDto {
  return {
    kind,
    taskId: id,
    title,
    status: "NotStarted",
    priority: "Normal",
    containerName: "Inbox",
    date: today,
    time: null,
    endTime: null,
    isBlock: false,
    isEstimated: false,
    estimateMinutes: null,
    category: "Personal",
    ...patch,
  };
}
const day = (date: string, patch: Partial<CalendarDayDto> = {}): CalendarDayDto => ({
  date,
  availableMinutes: 480,
  workAllowanceMinutes: null,
  busyMinutes: 0,
  plannedMinutes: 0,
  workMinutes: 0,
  unknownEstimates: 0,
  openCount: 0,
  overTotal: false,
  overWork: false,
  ...patch,
});

function fakes(entries: CalendarEntryDto[], unscheduled: QueryRowResponse[] = [], tasks: TaskResponse[] = [], editOk = true) {
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const api = {
    range: async (request: Record<string, unknown>): Promise<CalendarRangeResponse> => {
      calls.push({ method: "range", request });
      return {
        ok: true,
        error: null,
        from: request.from as string,
        to: request.to as string,
        today,
        zoneId: "Europe/Paris",
        entries,
        meetings: [],
        days: [day(today, { plannedMinutes: 600, overTotal: true })],
        truncated: false,
      };
    },
    unscheduled: async (request: Record<string, unknown>) => {
      calls.push({ method: "unscheduled", request });
      return { ok: true, error: null, rows: unscheduled, totalCount: unscheduled.length, nextCursor: null };
    },
  } as unknown as NonNullable<Parameters<typeof Calendar>[0]["api"]>;
  const taskApi = {
    get: async (request: { taskId: string }) => {
      calls.push({ method: "get", request });
      const found = tasks.find((item) => item.id === request.taskId);
      return { ok: !!found, task: found ?? null, error: found ? null : { message: "gone" } };
    },
    edit: async (request: Record<string, unknown>) => {
      calls.push({ method: "edit", request });
      return editOk
        ? { ok: true, task: tasks[0], error: null }
        : { ok: false, task: null, error: { message: "This task changed elsewhere." } };
    },
  } as unknown as NonNullable<Parameters<typeof Calendar>[0]["taskApi"]>;
  return { api, taskApi, calls };
}

function mount(
  api: ReturnType<typeof fakes>["api"],
  taskApi: ReturnType<typeof fakes>["taskApi"],
  extra: { onOpen?: (task: TaskResponse) => void; onChanged?: () => void; focus?: "Work" | "Personal" | null } = {},
) {
  return render(
    <Calendar
      tags={[]}
      containers={[]}
      focus={extra.focus ?? null}
      busy={false}
      revision={0}
      today={today}
      onOpen={extra.onOpen ?? (() => {})}
      onChanged={extra.onChanged ?? (() => {})}
      api={api}
      taskApi={taskApi}
    />,
    { container: dom.window.document.body },
  );
}

test("a timed block with an estimate ends at start plus estimate; due entries and untimed ones are all-day", () => {
  const block = toEvent(entry("Planned", "a", "Write", { time: "09:00", endTime: "10:30", isBlock: true }));
  assert.equal(block.start, `${today}T09:00:00`);
  assert.equal(block.end, `${today}T10:30:00`);
  assert.equal(block.allDay, false);
  assert.ok(block.classNames.includes("chaos-event-block"));
  assert.equal(block.editable, true);
  const due = toEvent(entry("Due", "a", "Write", { time: "17:00" }));
  assert.equal(due.allDay, true);
  assert.equal(due.start, today);
  assert.equal(due.editable, false);
  assert.ok(due.classNames.includes("chaos-event-due"));
  const repeat = toEvent(entry("Repeat", "a", "Water", { time: "08:00", endTime: "08:30" }));
  assert.equal(repeat.editable, false);
  assert.equal(repeat.allDay, false);
  assert.equal(addDays("2026-02-28", 1), "2026-03-01");
  assert.equal(addDays("2026-01-01", -1), "2025-12-31");
  assert.equal(dayBadge(day(today)), null);
  assert.match(dayBadge(day(today, { plannedMinutes: 600, overTotal: true }))!.reason, /10 h/);
});

test("the range is read with the focus and the filters, and an overloaded day carries a badge", async () => {
  const { api, taskApi, calls } = fakes([entry("Planned", "a", "Write report")]);
  mount(api, taskApi, { focus: "Work" });
  await screen.findByText("Write report");
  const range = calls.find((call) => call.method === "range")!.request as { from: string; to: string; filters: Record<string, unknown> };
  assert.equal(range.filters.category, "Work");
  assert.equal(range.filters.showPlanned, true);
  assert.equal(range.filters.showCompleted, false);
  assert.ok(range.from <= today && today <= range.to);
  await waitFor(() => assert.ok(document.querySelector(`[data-overload="${today}"]`)));
  assert.match(document.querySelector(`[data-overload="${today}"]`)!.getAttribute("aria-label") ?? "", /Overloaded/);
});

test("a planned entry opens a menu whose Move to date saves the planned date and time through the ordinary edit", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const planned = task("a", "Write report", { dueDate: "2030-01-01", dueTime: null });
  const changed: number[] = [];
  const { api, taskApi, calls } = fakes([entry("Planned", "a", "Write report")], [], [planned]);
  mount(api, taskApi, { onChanged: () => changed.push(1) });
  await user.click(await screen.findByText("Write report"));
  const menu = await screen.findByRole("region", { name: "Selected entry" });
  await user.click(within(menu).getByRole("button", { name: "Move to date…" }));
  const dialog = screen.getByRole("dialog", { name: "Move to date" });
  const date = within(dialog).getByLabelText("Planned date") as HTMLInputElement;
  await user.clear(date);
  await user.type(date, "2026-12-03");
  await user.type(within(dialog).getByLabelText("Time (optional)"), "14:30");
  await user.click(within(dialog).getByRole("button", { name: "Save" }));
  await waitFor(() => assert.ok(calls.some((call) => call.method === "edit")));
  const edit = calls.find((call) => call.method === "edit")!.request;
  assert.equal(edit.taskId, "a");
  assert.equal(edit.expectedVersion, "7");
  assert.equal(edit.plannedDate, "2026-12-03");
  assert.equal(edit.plannedTime, "14:30");
  assert.equal(edit.plannedZoneId, "Europe/Paris");
  // Due dates are carried through untouched.
  assert.equal(edit.dueDate, "2030-01-01");
  await waitFor(() => assert.equal(changed.length, 1));
  assert.equal(screen.queryByRole("dialog", { name: "Move to date" }), null);
});

test("a refused move keeps the dialog open and says why", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, taskApi } = fakes([entry("Planned", "a", "Write report")], [], [task("a", "Write report")], false);
  mount(api, taskApi);
  await user.click(await screen.findByText("Write report"));
  await user.click(await screen.findByRole("button", { name: "Move to date…" }));
  await user.click(screen.getByRole("button", { name: "Save" }));
  await screen.findAllByText("This task changed elsewhere.");
  assert.ok(screen.getByRole("dialog", { name: "Move to date" }));
});

test("a due marker opens its task directly instead of offering to move it", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const opened: string[] = [];
  const { api, taskApi } = fakes([entry("Due", "d", "File taxes")], [], [task("d", "File taxes", { dueDate: today })]);
  mount(api, taskApi, { onOpen: (item) => opened.push(item.id) });
  await user.click(await screen.findByText("File taxes"));
  await waitFor(() => assert.deepEqual(opened, ["d"]));
  assert.equal(screen.queryByRole("region", { name: "Selected entry" }), null);
});

test("an unscheduled task is planned with the keyboard: Plan… sets a date and clears the time", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const loose = task("u", "Book dentist");
  const row: QueryRowResponse = {
    task: loose,
    containerName: "Inbox",
    containerArchived: false,
    tagIds: [],
    matches: [],
    groupDate: null,
    waitingPerson: null,
    nextFollowUpAtUtc: null,
    lastActivityAtUtc: null, delegatedTo: null,
  };
  const { api, taskApi, calls } = fakes([], [row], [loose]);
  mount(api, taskApi);
  await screen.findByText("Book dentist");
  await user.click(screen.getByRole("button", { name: "Plan “Book dentist”" }));
  const dialog = screen.getByRole("dialog", { name: "Plan task" });
  const date = within(dialog).getByLabelText("Planned date") as HTMLInputElement;
  await user.clear(date);
  await user.type(date, "2026-12-05");
  await user.click(within(dialog).getByRole("button", { name: "Save" }));
  await waitFor(() => assert.ok(calls.some((call) => call.method === "edit")));
  const edit = calls.find((call) => call.method === "edit")!.request;
  assert.equal(edit.plannedDate, "2026-12-05");
  assert.equal(edit.plannedTime, null);
  assert.equal(edit.plannedZoneId, null);
});

test("turning off Planned and Due is sent as filters, and the grid keys move a highlighted day", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { api, taskApi, calls } = fakes([]);
  mount(api, taskApi);
  await waitFor(() => assert.ok(calls.some((call) => call.method === "range")));
  await user.click(screen.getByLabelText("Due"));
  await waitFor(() =>
    assert.equal((calls.filter((call) => call.method === "range").at(-1)!.request.filters as { showDue: boolean }).showDue, false),
  );
  const grid = screen.getByRole("group", { name: /Calendar grid/ });
  grid.focus();
  await user.keyboard("{ArrowRight}");
  await waitFor(() => assert.ok(document.querySelector(".chaos-cursor")));
});

test("selecting a space filters both calendar and unscheduled requests", async () => {
  const { api, taskApi, calls } = fakes([]);
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <SpacesProvider spaces={[{ spaceId: "team", name: "Team", kind: "Shared", role: 2, memberCount: 2 }]}>
      <Calendar
        tags={[]}
        containers={[]}
        focus={null}
        busy={false}
        revision={0}
        today={today}
        onOpen={() => {}}
        onChanged={() => {}}
        api={api}
        taskApi={taskApi}
      />
    </SpacesProvider>,
    { container: dom.window.document.body },
  );
  await user.selectOptions(screen.getByLabelText("Space"), "team");
  await waitFor(() => {
    assert.ok(calls.some((call) => call.method === "range" && (call.request.filters as { spaceId?: string }).spaceId === "team"));
    assert.ok(calls.some((call) => call.method === "unscheduled" && (call.request.filters as { spaceId?: string }).spaceId === "team"));
  });
});
