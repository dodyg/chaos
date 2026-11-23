import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import type { DelegationDto, DelegationItemDto, PersonResponse, TaskResponse, waitingRpc } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  crypto: { value: webcrypto, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { DelegationSection, DelegateDialog, NudgeDialog } = await import("./DelegationPanel");
type DelegationApi = import("./DelegationPanel").DelegationApi;
const { FollowUps } = await import("./Reminders");

const me = "11111111-1111-4111-8111-111111111111";
const omar = "22222222-2222-4222-8222-222222222222";
const shared = "33333333-3333-4333-8333-333333333333";

function task(): TaskResponse {
  return {
    id: "t1",
    title: "Send the quote",
    spaceId: shared,
    containerId: "c1",
    notes: "",
    status: "NotStarted",
    plannedDate: null,
    dueDate: null,
    version: "4",
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
  };
}

function person(id: string, name: string, isMe: boolean, role = 2): PersonResponse {
  return { actorId: `person:${id}`, displayName: name, initials: name[0], colorIndex: 0, kind: "person", isMe, role, former: false };
}

function delegation(patch: Partial<DelegationDto> = {}): DelegationDto {
  return {
    id: "d1",
    taskId: "t1",
    fromMemberId: omar,
    toMemberId: me,
    message: "Please handle the venue",
    doneDefinition: "Quote sent",
    dueDate: "2026-11-30",
    reviewRequired: true,
    state: "Pending",
    stateChangedAtUtc: "2026-11-23T09:00:00Z",
    response: null,
    version: "1",
    ...patch,
  };
}

function item(
  patch: Partial<DelegationDto> = {},
  nudges: DelegationItemDto["nudges"] = [],
  names: { from: string; to: string } = { from: "Omar", to: "Sara" },
): DelegationItemDto {
  const current = delegation(patch);
  return {
    delegation: current,
    task: task(),
    containerName: "Launch",
    fromName: names.from,
    toName: names.to,
    followUp: null,
    followUpDueAtUtc: null,
    followUpOverdue: false,
    nudges,
  };
}

type Call = Record<string, unknown>;
const ok = (patch: Record<string, unknown>) => ({ ok: true, error: null, ...patch });

function fakeApi(live: DelegationItemDto[], history: DelegationItemDto[] = []) {
  const calls: Call[] = [];
  const api = {
    get: async () => ok({ task: task(), live, history }),
    list: async () => ok({ items: [] }),
    nudgeTemplate: async () => ok({ text: 'Hi Omar, any news on "Send the quote"?' }),
    delegate: async (request: Call) => {
      calls.push({ method: "delegate", ...request });
      return ok({ delegation: delegation({ fromMemberId: me, toMemberId: omar }), task: task(), replayed: false });
    },
    respond: async (request: Call) => {
      calls.push({ method: "respond", ...request });
      return ok({ delegation: delegation({ state: "Accepted" }), task: task(), replayed: false });
    },
    done: async (request: Call) => {
      calls.push({ method: "done", ...request });
      return ok({ delegation: delegation({ state: "Done" }), task: task(), replayed: false });
    },
    close: async (request: Call) => {
      calls.push({ method: "close", ...request });
      return ok({ delegation: delegation({ state: "Closed" }), task: task(), replayed: false });
    },
    reopen: async (request: Call) => {
      calls.push({ method: "reopen", ...request });
      return ok({ delegation: delegation({ state: "Accepted" }), task: task(), replayed: false });
    },
    withdraw: async (request: Call) => {
      calls.push({ method: "withdraw", ...request });
      return ok({ delegation: delegation({ state: "Withdrawn" }), task: task(), replayed: false });
    },
    nudge: async (request: Call) => {
      calls.push({ method: "nudge", ...request });
      return ok({ delegation: delegation(), task: task(), replayed: false });
    },
  };
  return { api: api as unknown as DelegationApi, calls };
}

test("the delegation card shows the recipient's side and sends the move", async () => {
  const { api, calls } = fakeApi([item()]);
  const directoryApi = { list: async () => ({ ok: true, people: [person(me, "Sara", true), person(omar, "Omar", false)], error: null }) };
  const changed: (TaskResponse | null)[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <DelegationSection
      task={task()}
      canEdit={false}
      api={api}
      directoryApi={directoryApi}
      onTaskChanged={(value) => changed.push(value)}
    />,
    {
      container: dom.window.document.body,
    },
  );
  try {
    assert.ok(await screen.findByText("Omar"));
    assert.ok(screen.getByText(/waiting for your answer/));
    assert.ok(screen.getByText("Please handle the venue"));
    assert.ok(screen.getByText(/Done looks like: Quote sent/));
    assert.ok(screen.getByText("review needed"));
    assert.equal(screen.queryByRole("button", { name: /Delegate/ }), null);
    await user.click(screen.getByRole("button", { name: "Accept" }));
    await waitFor(() => assert.equal(calls.length, 1));
    assert.equal((calls[0] as { method: string }).method, "respond");
    assert.equal((calls[0] as { response: string }).response, "Accept");
    assert.equal((calls[0] as { delegationId: string }).delegationId, "d1");
    assert.equal((calls[0] as { expectedVersion: string }).expectedVersion, "1");
    assert.equal(changed.length, 1);
  } finally {
    cleanup();
  }
});

test("the delegator's card offers nudge, take back and the follow-up, and the nudge dialog sends the edited text", async () => {
  const nudges = [
    { id: "n1", text: "Any news?", sentAtUtc: new Date(Date.now() - 60 * 60 * 1000).toISOString() },
    { id: "n2", text: "Any news?", sentAtUtc: new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString() },
  ];
  const { api, calls } = fakeApi([item({ fromMemberId: me, toMemberId: omar, state: "Accepted" }, nudges, { from: "Sara", to: "Omar" })]);
  const directoryApi = { list: async () => ({ ok: true, people: [person(me, "Sara", true), person(omar, "Omar", false)], error: null }) };
  const user = userEvent.setup({ document: dom.window.document });
  render(<DelegationSection task={task()} canEdit api={api} directoryApi={directoryApi} onTaskChanged={() => {}} />, {
    container: dom.window.document.body,
  });
  try {
    assert.ok(await screen.findByText("Omar"));
    assert.ok(screen.getByText(/Delegated to/));
    assert.ok(screen.getByText(/Nudged 2 times \(2 in the last 24 hours\)/));
    assert.equal((screen.getByRole("button", { name: "Nudge" }) as HTMLButtonElement).disabled, false);
    assert.ok(screen.getByRole("button", { name: "Take back" }));
    assert.ok(screen.getByRole("button", { name: "Hand to someone else…" }));
    await user.click(screen.getByRole("button", { name: "Nudge" }));
    const textarea = (await screen.findByLabelText("Message")) as HTMLTextAreaElement;
    await waitFor(() => assert.equal(textarea.value, 'Hi Omar, any news on "Send the quote"?'));
    assert.ok(screen.getByText(/1 of 3 nudges left/));
    await user.clear(textarea);
    await user.type(textarea, "Any update?");
    await user.click(screen.getByRole("button", { name: "Send nudge" }));
    await waitFor(() => assert.equal(calls.length, 1));
    assert.equal((calls[0] as { method: string }).method, "nudge");
    assert.equal((calls[0] as { text: string }).text, "Any update?");
  } finally {
    cleanup();
  }
});

test("the delegator can close a reviewed result or send it back", async () => {
  const { api, calls } = fakeApi([item({ fromMemberId: me, toMemberId: omar, state: "Done" }, [], { from: "Sara", to: "Omar" })]);
  const directoryApi = { list: async () => ({ ok: true, people: [person(me, "Sara", true), person(omar, "Omar", false)], error: null }) };
  const user = userEvent.setup({ document: dom.window.document });
  render(<DelegationSection task={task()} canEdit api={api} directoryApi={directoryApi} onTaskChanged={() => {}} />, {
    container: dom.window.document.body,
  });
  try {
    assert.ok(await screen.findByText(/waiting for your review/));
    await user.click(screen.getByRole("button", { name: "Accept result" }));
    await waitFor(() => assert.equal(calls.length, 1));
    assert.equal((calls[0] as { method: string }).method, "close");
    assert.ok(screen.getByRole("button", { name: "Send back" }));
  } finally {
    cleanup();
  }
});

test("declining asks for a reason and sends it", async () => {
  const { api, calls } = fakeApi([item()]);
  const directoryApi = { list: async () => ({ ok: true, people: [person(me, "Sara", true), person(omar, "Omar", false)], error: null }) };
  const user = userEvent.setup({ document: dom.window.document });
  render(<DelegationSection task={task()} canEdit={false} api={api} directoryApi={directoryApi} onTaskChanged={() => {}} />, {
    container: dom.window.document.body,
  });
  try {
    await screen.findByText("Omar");
    await user.click(screen.getByRole("button", { name: "Decline" }));
    const reason = (await screen.findByLabelText("Why are you declining?")) as HTMLTextAreaElement;
    await user.type(reason, "Too busy");
    await user.click(screen.getByRole("button", { name: "Decline", hidden: false }));
    await waitFor(() => assert.equal(calls.length, 1));
    assert.equal((calls[0] as { response: string }).response, "Decline");
    assert.equal((calls[0] as { text: string }).text, "Too busy");
  } finally {
    cleanup();
  }
});

test("a refused decline keeps the reason for retry", async () => {
  const { api: baseApi } = fakeApi([item()]);
  const api: DelegationApi = {
    ...baseApi,
    respond: async () => ({ ok: false, error: { message: "Please retry" }, delegation: null, task: null, replayed: false }) as never,
  };
  const directoryApi = { list: async () => ({ ok: true, people: [person(me, "Sara", true), person(omar, "Omar", false)], error: null }) };
  const user = userEvent.setup({ document: dom.window.document });
  render(<DelegationSection task={task()} canEdit={false} api={api} directoryApi={directoryApi} onTaskChanged={() => {}} />, {
    container: dom.window.document.body,
  });
  try {
    await screen.findByText("Omar");
    await user.click(screen.getByRole("button", { name: "Decline" }));
    await user.type(screen.getByLabelText("Why are you declining?"), "Too busy");
    await user.click(screen.getByRole("button", { name: "Decline" }));
    await screen.findByText("Please retry");
    assert.equal((screen.getByLabelText("Why are you declining?") as HTMLTextAreaElement).value, "Too busy");
  } finally {
    cleanup();
  }
});

test("the delegate dialog sends the brief, review and follow-up", async () => {
  const { api, calls } = fakeApi([]);
  const user = userEvent.setup({ document: dom.window.document });
  const closed: boolean[] = [];
  render(
    <DelegateDialog
      task={task()}
      people={[person(me, "Sara", true, 1), person(omar, "Omar", false), person("v", "Vera", false, 3)]}
      api={api}
      onClose={() => closed.push(true)}
      onDelegated={() => {}}
    />,
    { container: dom.window.document.body },
  );
  try {
    const member = (await screen.findByLabelText("Delegate to")) as HTMLSelectElement;
    assert.deepEqual(
      [...member.options].map((option) => option.value).filter(Boolean),
      [omar],
      "viewers and the person themself are not offered",
    );
    await user.selectOptions(member, omar);
    await user.type(screen.getByLabelText("Message (optional)"), "Please handle the venue");
    await user.type(screen.getByLabelText("Done looks like (optional)"), "Quote sent");
    await user.type(screen.getByLabelText("Due date (optional)"), "2026-11-30");
    assert.equal((screen.getByRole("checkbox", { name: /I review/ }) as HTMLInputElement).checked, true);
    await user.selectOptions(screen.getByLabelText("Follow up"), "Fixed");
    await user.type(screen.getByLabelText("Follow-up time (for “At a time”)"), "2026-11-25T09:00");
    await user.click(screen.getByRole("button", { name: "Delegate" }));
    await waitFor(() => assert.equal(calls.length, 1));
    const request = calls[0] as Record<string, unknown>;
    assert.equal(request.method, "delegate");
    assert.equal(request.toMemberId, omar);
    assert.equal(request.message, "Please handle the venue");
    assert.equal(request.doneDefinition, "Quote sent");
    assert.equal(request.dueDate, "2026-11-30");
    assert.equal(request.reviewRequired, true);
    assert.equal(request.expectedVersion, "4");
    assert.deepEqual(request.followUp, { kind: "Fixed", fixedAtUtc: new Date("2026-11-25T09:00").toISOString(), amount: null, unit: null });
  } finally {
    cleanup();
  }
});

test("the nudge dialog counts the last 24 hours and blocks at the limit", async () => {
  const { api, calls } = fakeApi([]);
  const sent: number[] = [];
  const recent = [0, 1, 2].map((hours) => ({
    id: `n${hours}`,
    text: "x",
    sentAtUtc: new Date(Date.now() - hours * 60 * 60 * 1000).toISOString(),
  }));
  render(
    <NudgeDialog delegationId="d1" version="2" toName="Omar" nudges={recent} api={api} onClose={() => {}} onSent={() => sent.push(1)} />,
    {
      container: dom.window.document.body,
    },
  );
  try {
    assert.ok(await screen.findByText(/3 times in the last 24 hours/));
    assert.equal((screen.getByRole("button", { name: "Send nudge" }) as HTMLButtonElement).disabled, true);
    assert.equal(calls.length, 0);
  } finally {
    cleanup();
  }
});

test("a delegation follow-up in My Day offers nudge and take back", async () => {
  const { api, calls } = fakeApi([item({ fromMemberId: me, toMemberId: omar, state: "Accepted" }, [], { from: "Sara", to: "Omar" })]);
  const waitingApi = {
    due: async () => ({
      ok: true,
      error: null,
      items: [
        {
          task: task(),
          containerName: "Launch",
          person: null,
          dueAtUtc: "2026-11-23T10:00:00Z",
          overdue: true,
          kind: "Fixed",
          delegationId: "d1",
          delegatedTo: "Omar",
        },
      ],
    }),
  };
  const changed: number[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  const directoryApi = { list: async () => ({ ok: true, people: [person(me, "Sara", true), person(omar, "Omar", false)], error: null }) };
  render(
    <FollowUps
      day="2026-11-23"
      revision={0}
      busy={false}
      api={waitingApi as unknown as typeof waitingRpc}
      delegationApi={api}
      directoryApi={directoryApi}
      onOpen={() => {}}
      onChanged={() => changed.push(1)}
    />,
    {
      container: dom.window.document.body,
    },
  );
  try {
    assert.ok(await screen.findByText("Send the quote"));
    assert.ok(screen.getByText("Omar"));
    assert.ok(screen.getByRole("button", { name: "Nudge" }));
    await user.click(screen.getByRole("button", { name: "Hand to someone else" }));
    assert.ok(await screen.findByLabelText("Delegate to"));
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: "Take back" }));
    await waitFor(() => assert.equal(calls.length, 1));
    assert.equal((calls[0] as { method: string }).method, "withdraw");
    assert.equal((calls[0] as { expectedVersion: string }).expectedVersion, "1");
    assert.equal(changed.length, 1);
  } finally {
    cleanup();
  }
});
