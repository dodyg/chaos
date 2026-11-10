import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { journalRpc, type CarryOverRequest, type SummaryDetailDto, type SummaryFactsDto, type SummaryHeaderDto, type SummaryTaskDto } from "#neoastra";

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
const { Journal, SummaryPrompts, countsText } = await import("./Journal");

function task(id: string, title: string, containerName = "Inbox"): SummaryTaskDto {
  return { taskId: id, title, containerId: "c", containerName, containerKind: "List", status: "NotStarted", priority: "Normal", plannedDate: null, dueDate: null, dueTime: null, estimateMinutes: null };
}

function header(patch: Partial<SummaryHeaderDto> = {}): SummaryHeaderDto {
  return {
    id: "s1", kind: "Daily", periodStart: "2026-11-10", periodEnd: "2026-11-10", revision: "1", capturedThroughUtc: "2026-11-10T18:00:00Z", partial: true,
    trigger: "Scheduled", generatedAtUtc: "2026-11-10T18:00:00Z", zoneId: "UTC",
    counts: { completionEvents: 2, stillDone: 1, unfinished: 1, diaryUpdates: 1, deadlines: 0, waiting: 0, selected: 0, quiet: false }, ...patch,
  };
}

function facts(): SummaryFactsDto {
  return {
    completions: [
      { task: task("a", "Plant bulbs", "Garden"), completedAtUtc: "2026-11-10T10:00:00Z", reopenedAfter: false, stillDone: true },
      { task: task("b", "Draft report", "Work"), completedAtUtc: "2026-11-10T11:00:00Z", reopenedAfter: true, stillDone: false },
    ],
    projects: [{ projectId: "p", name: "Garden", doneBefore: 0, countedBefore: 2, doneAfter: 1, countedAfter: 2 }],
    unfinished: [{ task: task("c", "Tidy shed"), source: "MyDay" }],
    waiting: [], deadlines: [], diaryUpdates: [{ entryId: "d", taskId: "a", taskTitle: "Plant bulbs", entryAtUtc: "2026-11-10T10:05:00Z", excerpt: "Forty tulips." }],
    diaryUpdateCount: 1, suggested: [], week: null, selected: [], overdueCount: 0, followUps: [],
  };
}

const detail: SummaryDetailDto = { header: header(), facts: facts(), revisions: [{ id: "s1", revision: "1", generatedAtUtc: "2026-11-10T18:00:00Z", capturedThroughUtc: "2026-11-10T18:00:00Z", trigger: "Scheduled" }], isLatest: true };

test("journal counts read in plain words and a quiet period says so", () => {
  assert.equal(countsText(header()), "1 completed · 1 unfinished · 1 diary update");
  assert.equal(countsText(header({ counts: { ...header().counts, quiet: true } })), "A quiet period: nothing recorded.");
});

test("a summary groups completions by list, marks reopened ones and carries over only the chosen tasks", async () => {
  const carried: CarryOverRequest[] = [];
  const api = {
    ...journalRpc,
    list: async () => ({ ok: true, entries: [header()], nextCursor: null, error: null }),
    get: async () => ({ ok: true, summary: detail, error: null }),
    settings: async () => ({ ok: true, morningTime: "08:00", eveningTime: "18:00", weeklyDay: "Saturday", firstWeekday: "Sunday", version: "1", zoneId: "UTC", error: null }),
    previewCarryOver: async () => ({ ok: true, target: "Day", targetDate: "2026-11-11", candidates: [{ task: task("c", "Tidy shed"), stillOpen: true, alreadyPlanned: false }], error: null }),
    carryOver: async (request: CarryOverRequest) => { carried.push(request); return { ok: true, target: "Day", targetDate: "2026-11-11", added: 1, skipped: 0, replayed: false, error: null }; },
  } as unknown as typeof journalRpc;
  const user = userEvent.setup({ document: dom.window.document });
  render(<Journal api={api} revision={0} today="2026-11-10" openId={null} onOpenTask={() => {}} onChanged={() => {}}/>);

  await screen.findByRole("heading", { name: /Evening summary · / });
  assert.ok(screen.getByText("Garden", { selector: "h5" }));
  assert.ok(screen.getByText("reopened later"));
  assert.ok(screen.getByText(/Through /));
  assert.ok(screen.getByText(/Garden: 0 of 2 → 1 of 2 done/));
  assert.ok(screen.getByText("Forty tulips."));

  await user.click(screen.getByRole("button", { name: /Carry over to tomorrow/ }));
  const box = await screen.findByRole("checkbox");
  assert.equal((box as HTMLInputElement).checked, true);
  await user.click(screen.getByRole("button", { name: "Carry 1 over" }));
  await waitFor(() => assert.equal(carried.length, 1));
  assert.deepEqual(carried[0].taskIds, ["c"]);
  await screen.findByText(/1 added to My Day/);
  cleanup();
});

test("a ready summary prompt opens the journal and is dismissed", async () => {
  const dismissed: string[] = [];
  const opened: string[] = [];
  const api = {
    ...journalRpc,
    prompts: async () => ({ ok: true, prompts: [header({ id: "s9" })], error: null }),
    dismiss: async (request: { summaryId: string }) => { dismissed.push(request.summaryId); return { ok: true, error: null }; },
  } as unknown as typeof journalRpc;
  const user = userEvent.setup({ document: dom.window.document });
  render(<SummaryPrompts api={api} intervalMs={60_000} onOpen={id => opened.push(id)}/>);

  await screen.findByText(/is ready/);
  await user.click(screen.getByRole("button", { name: "Open" }));
  await waitFor(() => assert.deepEqual(dismissed, ["s9"]));
  assert.deepEqual(opened, ["s9"]);
  assert.equal(screen.queryByText(/is ready/), null);
  cleanup();
});
