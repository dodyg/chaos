import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { reminderRpc, waitingRpc, type NotificationActRequest, type NotificationItemResponse, type ReminderAddRequest, type TaskResponse, type WaitingSetRequest } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { AlertStack, FollowUps, TaskReminders, TaskWaiting, describeRule } = await import("./Reminders");

function task(patch: Partial<TaskResponse> = {}): TaskResponse {
  return { id: "t1", title: "Dentist", spaceId: "space", containerId: "inbox", notes: "", status: "NotStarted", plannedDate: null, dueDate: "2026-11-12", version: "3", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: "14:00:00", dueZoneId: "UTC", sectionId: null, ...patch };
}

function alert(patch: Partial<NotificationItemResponse> = {}): NotificationItemResponse {
  return { jobId: "j1", generation: "2", taskId: "t1", taskTitle: "Dentist", taskOpen: true, source: "ReminderRule", person: null, targetAtUtc: "2026-11-12T13:00:00Z", deliveredAtUtc: "2026-11-12T13:00:00Z", state: "Delivered", presentation: "InApp", actionTaken: null, late: false, repeat: false, ...patch };
}

test("rules read in plain words, with negative offsets before the anchor", () => {
  assert.equal(describeRule({ anchor: "Due", offsetMinutes: -60, absoluteAtUtc: null, insistentMinutes: null, enabled: true }), "1 hour before the due time");
  assert.equal(describeRule({ anchor: "Planned", offsetMinutes: 2880, absoluteAtUtc: null, insistentMinutes: null, enabled: true }), "2 days after the planned time");
  assert.equal(describeRule({ anchor: "Due", offsetMinutes: 0, absoluteAtUtc: null, insistentMinutes: null, enabled: true }), "At the due time");
});

test("adding a reminder sends a signed offset and shows a paused rule's reason", async () => {
  const added: ReminderAddRequest[] = [];
  const api = {
    ...reminderRpc,
    list: async () => ({ ok: true, rules: [{ id: "r1", taskId: "t1", version: "1", rule: { anchor: "Planned", offsetMinutes: -30, absoluteAtUtc: null, insistentMinutes: null, enabled: true }, targetAtUtc: null, nextDeliveryAtUtc: null, suspension: "NoPlannedDate", lastDeliveredAtUtc: null }], error: null }),
    add: async (request: ReminderAddRequest) => { added.push(request); return { ok: true, replayed: false, error: null }; },
  } as unknown as typeof reminderRpc;
  const user = userEvent.setup({ document: dom.window.document });
  let changed = 0;
  render(<TaskReminders task={task()} revision={0} busy={false} api={api} onChanged={() => changed++}/>);

  assert.ok(await screen.findByText("Paused: the task has no planned date"));
  await user.clear(screen.getByLabelText("Amount"));
  await user.type(screen.getByLabelText("Amount"), "2");
  await user.click(screen.getByLabelText("Repeat until I act"));
  await user.click(screen.getByRole("button", { name: /Add reminder/ }));

  await waitFor(() => assert.equal(added.length, 1));
  assert.deepEqual(added[0].rule, { anchor: "Due", offsetMinutes: -120, absoluteAtUtc: null, insistentMinutes: 10, enabled: true });
  assert.equal(changed, 1);
  cleanup();
});

test("an alert's Done asks about open subtasks, then completes with the choice and the shown generation", async () => {
  const acts: NotificationActRequest[] = [];
  let remaining = [alert()];
  const api = {
    ...reminderRpc,
    alerts: async () => ({ ok: true, items: remaining, error: null }),
    act: async (request: NotificationActRequest) => {
      acts.push(request);
      if (request.openSubtaskChoice === null) return { ok: false, outcome: null, taskId: null, snoozedUntilUtc: null, replayed: false, error: { kind: "Validation", message: "Open subtasks", fields: [{ fieldPath: "openSubtaskChoice", message: "Choose" }], correlationId: "x" } };
      remaining = [];
      return { ok: true, outcome: "Completed", taskId: "t1", snoozedUntilUtc: null, replayed: false, error: null };
    },
  } as unknown as typeof reminderRpc;
  const user = userEvent.setup({ document: dom.window.document });
  let changed = 0;
  render(<AlertStack api={api} intervalMs={60_000} onOpen={() => undefined} onChanged={() => changed++}/>);

  await user.click(await screen.findByRole("button", { name: /Done/ }));
  await user.click(await screen.findByRole("button", { name: "Keep them open" }));

  await waitFor(() => assert.equal(screen.queryByRole("region", { name: "Reminders" }), null));
  assert.deepEqual(acts.map(act => [act.action, act.generation, act.openSubtaskChoice]), [["Done", "2", null], ["Done", "2", "KeepOpen"]]);
  assert.notEqual(acts[0].operationId, acts[1].operationId);
  assert.equal(changed, 1);
  cleanup();
});

test("snoozing and opening from an alert, and a missed alert says so", async () => {
  const acts: NotificationActRequest[] = [];
  const api = {
    ...reminderRpc,
    alerts: async () => ({ ok: true, items: [alert({ late: true, source: "FollowUp", person: "Omar" })], error: null }),
    act: async (request: NotificationActRequest) => { acts.push(request); return { ok: true, outcome: request.action === "Open" ? "Opened" : "Snoozed", taskId: "t1", snoozedUntilUtc: null, replayed: false, error: null }; },
  } as unknown as typeof reminderRpc;
  const user = userEvent.setup({ document: dom.window.document });
  const opened: string[] = [];
  render(<AlertStack api={api} intervalMs={60_000} onOpen={id => opened.push(id)} onChanged={() => undefined}/>);

  assert.ok(await screen.findByText("Missed while Chaos was closed or asleep."));
  assert.ok(screen.getByText("Omar"));
  await user.click(screen.getByRole("button", { name: "1 hour" }));
  await user.click(screen.getByRole("button", { name: /Open/ }));

  await waitFor(() => assert.deepEqual(opened, ["t1"]));
  assert.deepEqual(acts.map(act => [act.action, act.snooze]), [["Snooze", "OneHour"], ["Open", null]]);
  cleanup();
});

test("waiting on someone saves the person and an inactivity follow-up, and says nothing is sent", async () => {
  const sets: WaitingSetRequest[] = [];
  const api = {
    ...waitingRpc,
    get: async () => ({ ok: true, task: task(), person: null, startedAtUtc: null, followUp: null, followUpDueAtUtc: null, followUpSnoozedUntilUtc: null, followUpSuspension: "None", lastActivityAtUtc: null, replayed: false, error: null }),
    set: async (request: WaitingSetRequest) => {
      sets.push(request);
      return { ok: true, task: task({ status: "WaitingOnSomeone", version: "4" }), person: request.person, startedAtUtc: "2026-11-10T09:00:00Z", followUp: request.followUp, followUpDueAtUtc: "2026-11-13T09:00:00Z", followUpSnoozedUntilUtc: null, followUpSuspension: "None", lastActivityAtUtc: "2026-11-10T09:00:00Z", replayed: false, error: null };
    },
  } as unknown as typeof waitingRpc;
  const user = userEvent.setup({ document: dom.window.document });
  const saved: (TaskResponse | null)[] = [];
  render(<TaskWaiting task={task()} revision={0} busy={false} api={api} onChanged={result => saved.push(result)}/>);

  assert.ok(await screen.findByText("Kept privately with this task. Chaos never contacts this person."));
  await user.type(screen.getByLabelText("Person"), "Omar");
  await user.selectOptions(screen.getByLabelText("Follow-up rule"), "AfterInactivity");
  await user.click(screen.getByRole("button", { name: "Start waiting" }));

  await waitFor(() => assert.equal(sets.length, 1));
  assert.deepEqual({ person: sets[0].person, version: sets[0].expectedVersion, followUp: sets[0].followUp }, { person: "Omar", version: "3", followUp: { kind: "AfterInactivity", fixedAtUtc: null, amount: 3, unit: "Days" } });
  assert.equal(saved[0]?.status, "WaitingOnSomeone");
  assert.ok(await screen.findByText(/After 3 days without activity/));
  cleanup();
});

test("My Day follow-ups can be logged privately and snoozed", async () => {
  const calls: string[] = [];
  let items = [{ task: task({ status: "WaitingOnSomeone" }), containerName: "Inbox", person: "Lina", dueAtUtc: "2026-11-10T08:00:00Z", overdue: true, kind: "Fixed" }];
  const response = { ok: true, task: task(), person: "Lina", startedAtUtc: null, followUp: null, followUpDueAtUtc: null, followUpSnoozedUntilUtc: null, followUpSuspension: "None", lastActivityAtUtc: null, replayed: false, error: null };
  const api = {
    ...waitingRpc,
    due: async () => ({ ok: true, items, error: null }),
    log: async (request: { note: string }) => { calls.push(`log:${request.note}`); return response; },
    snooze: async (request: { choice: string }) => { calls.push(`snooze:${request.choice}`); items = []; return response; },
  } as unknown as typeof waitingRpc;
  const user = userEvent.setup({ document: dom.window.document });
  render(<FollowUps day="2026-11-10" revision={0} busy={false} api={api} onOpen={() => undefined} onChanged={() => undefined}/>);

  assert.ok(await screen.findByText("Lina"));
  await user.click(screen.getByRole("button", { name: /Log/ }));
  await user.type(screen.getByLabelText("Follow-up note for Dentist"), "Left a voicemail");
  await user.click(screen.getByRole("button", { name: "Save" }));
  await waitFor(() => assert.deepEqual(calls, ["log:Left a voicemail"]));
  await waitFor(() => assert.equal((screen.getByRole("button", { name: /Tomorrow/ }) as HTMLButtonElement).disabled, false));
  await user.click(screen.getByRole("button", { name: /Tomorrow/ }));

  await waitFor(() => assert.equal(screen.queryByRole("list", { name: "Follow-ups" }), null));
  assert.deepEqual(calls, ["log:Left a voicemail", "snooze:Tomorrow"]);
  cleanup();
});
