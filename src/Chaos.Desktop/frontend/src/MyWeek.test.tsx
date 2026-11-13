import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { myWeekRpc, type DayClockResponse, type TaskResponse, type WeekBoardDto, type WeekDraftDto, type WeekOverviewDto } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { MyWeek, dayLabel, decideLeftover, placeTask } = await import("./MyWeek");
afterEach(() => cleanup());

const today = "2026-11-10";
const dates = ["2026-11-08", "2026-11-09", "2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13", "2026-11-14"];
const clock: DayClockResponse = { today, nextDayStartsAtUtc: "2026-11-11T00:00:00.0000000Z", zoneId: "UTC" };

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return { id, title, spaceId: "space", containerId: "inbox", notes: "", status: "NotStarted", plannedDate: null, dueDate: null, version: "1", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null, ...patch };
}

function board(placements: readonly { taskId: string; day: string | null }[] = [], overTuesday = false): WeekBoardDto {
  return {
    goals: [],
    placements: placements.map(item => ({ ...item, origin: "Planned" })),
    days: dates.map(date => ({ date, availableMinutes: 480, workAllowanceMinutes: null, isOverride: false, plannedMinutes: overTuesday && date === today ? 600 : 0, workMinutes: 0, unknownEstimates: 0, openCount: 0, overTotal: overTuesday && date === today, overWork: false })),
    pool: { plannedMinutes: 0, workMinutes: 0, unknownEstimates: 0, openCount: 0 },
  };
}

function overview(patch: Partial<WeekOverviewDto> = {}): WeekOverviewDto {
  return {
    weekStart: dates[0], weekEnd: dates[6], today, firstWeekday: "Sunday", planId: null, state: null, confirmedRevision: "0", confirmedAtUtc: null,
    editable: true, availabilityConfigured: true, confirmed: board(), draft: null, draftRevision: "0", draftBoard: null,
    lookBack: { weekStart: "2026-11-01", weekEnd: "2026-11-07", hadPlan: true, completedTaskIds: [], completedCount: 2, assignedCount: 3, assignedDone: 2, assignedCancelled: 0, unfinishedTaskIds: ["left"], goals: [], journalSummaryId: null, journalCompleted: [] },
    candidates: [{ taskId: "due", reason: "DueThisWeek" }], goalSuggestions: [{ title: "Finish Due report", projectId: null, taskIds: ["due"], reason: "ImportantTask" }],
    pendingReconciliation: [], checkIn: null,
    tasks: [{ task: task("left", "Left over"), containerName: "Inbox", containerArchived: false }, { task: task("due", "Due report", { dueDate: "2026-11-12" }), containerName: "Inbox", containerArchived: false }],
    ...patch,
  };
}

/** A fake week service: keeps the draft the way the server would and logs every call. */
function fakes(initial: WeekOverviewDto) {
  let current = initial;
  const calls: { method: string; request: Record<string, unknown> }[] = [];
  const respond = (week: WeekOverviewDto) => { current = week; return { ok: true, week, replayed: false, error: null }; };
  const api = {
    ...myWeekRpc,
    open: async () => ({ ok: true, week: current, error: null }),
    startDraft: async (request: Record<string, unknown>) => { calls.push({ method: "startDraft", request }); return respond({ ...current, draft: { goals: [], assignments: [], leftovers: [], overrides: [], step: 1 }, draftRevision: "1", draftBoard: board() }); },
    saveDraft: async (request: { draft: WeekDraftDto } & Record<string, unknown>) => {
      calls.push({ method: "saveDraft", request });
      return respond({ ...current, draft: request.draft, draftRevision: String(Number(current.draftRevision) + 1), draftBoard: board(request.draft.assignments) });
    },
    discardDraft: async (request: Record<string, unknown>) => { calls.push({ method: "discardDraft", request }); return respond({ ...current, draft: null, draftBoard: null }); },
    confirm: async (request: Record<string, unknown>) => {
      calls.push({ method: "confirm", request });
      return respond({ ...current, state: "Confirmed", confirmedRevision: "1", confirmed: board(current.draft?.assignments ?? []), draft: null, draftBoard: null });
    },
    reconcile: async (request: Record<string, unknown>) => { calls.push({ method: "reconcile", request }); return respond({ ...current, pendingReconciliation: [] }); },
    setGoalOutcome: async (request: Record<string, unknown>) => { calls.push({ method: "setGoalOutcome", request }); return respond(current); },
  } as unknown as typeof myWeekRpc;
  return { api, calls };
}

function show(api: typeof myWeekRpc, changed: () => void = () => {}) {
  return render(<MyWeek clock={clock} revision={0} busy={false} containers={[]} api={api} onOpen={() => {}} onChanged={changed}/>);
}

test("draft helpers keep one place per task and tie carry decisions to the pool", () => {
  const empty: WeekDraftDto = { goals: [], assignments: [], leftovers: [], overrides: [], step: 2 };
  const carried = decideLeftover(empty, "left", "Carry");
  assert.deepEqual(carried.assignments, [{ taskId: "left", day: null }]);
  const placed = placeTask(carried, "left", "2026-11-12");
  assert.deepEqual(placed.assignments, [{ taskId: "left", day: "2026-11-12" }]);
  const dropped = decideLeftover(placed, "left", "Remove");
  assert.deepEqual(dropped.assignments, []);
  assert.deepEqual(dropped.leftovers, [{ taskId: "left", decision: "Remove", laterDate: null }]);
  assert.equal(dayLabel("2026-11-10"), "Tue 10");
});

test("the ritual saves each step, carries a leftover, places it on a day and confirms the saved revision", async () => {
  const { api, calls } = fakes(overview());
  let changes = 0;
  show(api, () => changes++);
  const user = userEvent.setup({ document: dom.window.document });

  await user.click(await screen.findByRole("button", { name: "Plan this week" }));
  await screen.findByText("Last week, Sun 1 – Sat 7");
  await user.click(screen.getByRole("button", { name: "Next" }));

  await user.selectOptions(await screen.findByRole("combobox", { name: "Decision for Left over" }), "Carry");
  await user.click(screen.getByRole("button", { name: "Next" }));

  await user.click(await screen.findByRole("button", { name: "Add goal Finish Due report" }));
  assert.ok(screen.getByText("Two or three goals usually keep a week focused."));
  await user.click(screen.getByRole("button", { name: "Next" }));

  await user.selectOptions(await screen.findByRole("combobox", { name: "Day for Left over" }), "2026-11-12");
  await user.click(screen.getByRole("button", { name: "Add Due report to this week" }));
  await user.click(screen.getByRole("button", { name: "Next" }));

  await user.click(await screen.findByRole("button", { name: /Confirm plan/ }));
  await waitFor(() => assert.ok(calls.some(call => call.method === "confirm")));

  const lastSave = calls.filter(call => call.method === "saveDraft").at(-1)!.request as { draft: WeekDraftDto; expectedDraftRevision: string };
  assert.deepEqual(lastSave.draft.assignments, [{ taskId: "left", day: "2026-11-12" }, { taskId: "due", day: null }]);
  assert.deepEqual(lastSave.draft.leftovers, [{ taskId: "left", decision: "Carry", laterDate: null }]);
  assert.equal(lastSave.draft.goals[0].title, "Finish Due report");
  assert.deepEqual(lastSave.draft.goals[0].taskIds, ["due"]);
  const confirm = calls.find(call => call.method === "confirm")!.request;
  assert.equal(confirm.expectedDraftRevision, "5");
  assert.equal(changes, 1);
  await screen.findByRole("button", { name: "Review this week's plan" });
  cleanup();
});

test("cancelling planning discards the draft and shows the week unplanned again", async () => {
  const { api, calls } = fakes(overview({ draft: { goals: [], assignments: [{ taskId: "due", day: today }], leftovers: [], overrides: [], step: 4 }, draftRevision: "3", draftBoard: board() }));
  show(api);
  const user = userEvent.setup({ document: dom.window.document });
  await user.click(await screen.findByRole("button", { name: /Cancel planning/ }));
  await waitFor(() => assert.equal(calls.at(-1)?.method, "discardDraft"));
  assert.equal(calls.at(-1)!.request.expectedDraftRevision, "3");
  await screen.findByRole("button", { name: "Plan this week" });
  cleanup();
});

test("a confirmed week shows capacity warnings and lets the person choose a day comparison", async () => {
  const { api, calls } = fakes(overview({
    state: "Confirmed", confirmedRevision: "2", planId: "plan",
    confirmed: board([{ taskId: "due", day: today }], true),
    pendingReconciliation: [{ date: today, weekPlanId: "plan", revision: "2", addTaskIds: ["due"], removeTaskIds: ["left"] }],
  }));
  show(api);
  const user = userEvent.setup({ document: dom.window.document });
  const tuesday = await screen.findByRole("list", { name: "Tasks for Tue 10" });
  assert.ok(within(tuesday).getByText("Due report"));
  assert.ok(screen.getByText("More than this day holds"));

  await user.click(screen.getByRole("tab", { name: /Updates/ }));
  const comparison = screen.getByRole("region", { name: "Update Tue 10" });
  await user.click(within(comparison).getByRole("checkbox", { name: "Remove Left over" }));
  await user.click(within(comparison).getByRole("button", { name: "Update Tue 10" }));
  await waitFor(() => assert.equal(calls.at(-1)?.method, "reconcile"));
  assert.deepEqual(calls.at(-1)!.request.addTaskIds, ["due"]);
  assert.deepEqual(calls.at(-1)!.request.removeTaskIds, ["left"]);
  cleanup();
});

test("a long task title stays available on the weekly board", async () => {
  const title = "Investigate " + "VeryLongUnbrokenTaskTitle".repeat(8);
  const item = task("long", title, { status: "WaitingOnSomeone", priority: "High" });
  const { api } = fakes(overview({ state: "Confirmed", confirmedRevision: "2", planId: "plan", confirmed: board([{ taskId: item.id, day: today }]), tasks: [{ task: item, containerName: "Inbox", containerArchived: false }] }));
  const opened: string[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<MyWeek clock={clock} revision={0} busy={false} containers={[]} api={api} onOpen={task => opened.push(task.id)} onChanged={() => {}}/>);
  const list = await screen.findByRole("list", { name: "Tasks for Tue 10" });
  await user.click(within(list).getByRole("button", { name: title }));
  assert.deepEqual(opened, ["long"]);
  assert.ok(within(list).getByText("Waiting On Someone"));
});

test("week tabs separate the plan, updates and availability", async () => {
  const { api } = fakes(overview({ availabilityConfigured: false }));
  const user = userEvent.setup({ document: dom.window.document });
  show(api);
  await screen.findByRole("button", { name: "Plan this week" });
  assert.equal(screen.queryByRole("heading", { name: "How much time do you usually have?" }), null);
  await user.click(screen.getByRole("button", { name: "Set availability" }));
  assert.ok(screen.getByRole("heading", { name: "How much time do you usually have?" }));
  await user.click(screen.getByRole("tab", { name: /Updates/ }));
  assert.ok(screen.getByText("No updates for this week."));
  await user.click(screen.getByRole("tab", { name: "Plan" }));
  assert.ok(screen.getByRole("button", { name: "Plan this week" }));
});
