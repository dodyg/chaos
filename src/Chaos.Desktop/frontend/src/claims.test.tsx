import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import { agentRpc, claimRpc, type AgentViewDto, type PersonResponse, type TaskResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  crypto: { value: webcrypto, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { AgentSection, ClaimedLine, claimLive } = await import("./claims");

const me = "person:11111111-1111-4111-8111-111111111111";
const bot = "agent:11111111-1111-4111-8111-111111111113";
const minutes = (count: number) => new Date(Date.now() - count * 60_000).toISOString();
const inMinutes = (count: number) => new Date(Date.now() + count * 60_000).toISOString();

function task(patch: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id: "t1",
    title: "Draft the icon set",
    spaceId: "personal",
    containerId: "inbox",
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
    claimedById: null,
    claimedAtUtc: null,
    claimExpiresAtUtc: null,
    agentReady: false,
    agentAssigneeId: null,
    ...patch,
  };
}
const person = (displayName: string, kind: "person" | "agent", isMe = false): PersonResponse => ({
  actorId: kind === "agent" ? bot : me,
  displayName,
  initials: displayName[0],
  colorIndex: 0,
  kind,
  isMe,
  role: null,
  former: false,
});
/** A local agent; by default given the fixture task's list (inbox), so it can take the task. */
const agentView = (id: string, name: string, list = "inbox"): AgentViewDto => ({
  agent: { id, name, pausedAtUtc: null, version: "1" },
  lists: [{ containerId: list, containerName: list, allowed: true }],
  tokens: [],
});

type Call = Record<string, unknown>;

/** A claims service in memory: each call answers with the current task plus the change it makes, as the host does for a Personal task. */
function fakeClaims(
  options: { claims?: Partial<Record<"claim" | "release" | "takeOver" | "setReady", { error?: string; task?: null }>> } = {},
) {
  const calls: Call[] = [];
  const state = { current: task(), onChanged: 0 };
  const answer = (method: string, patch: Partial<TaskResponse>) => async (request: Call) => {
    calls.push({ method, ...request });
    state.current = { ...state.current, ...patch, version: String(Number(state.current.version) + 1) };
    const option = options.claims?.[method as "claim"];
    if (option?.error)
      return {
        ok: false,
        task: null,
        serviceStatus: null,
        replayed: false,
        error: { kind: "OperationConflict", message: option.error, fields: [], correlationId: "x" },
      };
    return {
      ok: true,
      task: option?.task === null ? null : state.current,
      serviceStatus: option?.task === null ? "InProgress" : null,
      replayed: false,
      error: null,
    };
  };
  const api = {
    ...claimRpc,
    claim: answer("claim", { claimedById: me, claimedAtUtc: minutes(0), claimExpiresAtUtc: inMinutes(120), status: "InProgress" }),
    release: answer("release", { claimedById: null, claimedAtUtc: null, claimExpiresAtUtc: null, status: "NotStarted", agentReady: true }),
    takeOver: answer("takeOver", { claimedById: me, claimedAtUtc: minutes(0), claimExpiresAtUtc: inMinutes(120) }),
    heartbeat: answer("heartbeat", {}),
    setReady: async (request: Call) => {
      calls.push({ method: "setReady", ...request });
      state.current = {
        ...state.current,
        agentReady: request.ready as boolean,
        agentAssigneeId: (request.agentId as string | null) ?? null,
        version: String(Number(state.current.version) + 1),
      };
      return { ok: true, task: state.current, serviceStatus: null, replayed: false, error: null };
    },
  };
  return { api: api as unknown as typeof claimRpc, calls, state };
}
const withoutOperation = (call: Call) => Object.fromEntries(Object.entries(call).filter(([key]) => key !== "operationId"));

function Harness({
  initial,
  api,
  people,
  agents,
  shared = false,
  canEdit = true,
}: {
  initial: TaskResponse;
  api: ReturnType<typeof fakeClaims>["api"];
  people: Record<string, PersonResponse>;
  agents?: AgentViewDto[];
  shared?: boolean;
  canEdit?: boolean;
}) {
  const [current, setCurrent] = React.useState(initial);
  return (
    <AgentSection
      task={current}
      canEdit={canEdit}
      shared={shared}
      claimApi={api}
      agentsApi={{ list: async () => ({ ok: true, agents: agents ?? [], error: null }) }}
      peopleApi={{ get: async ({ actorId }: { actorId: string }) => ({ ok: true, person: people[actorId] ?? null, error: null }) }}
      onTaskChanged={(saved) => setCurrent((value) => ({ ...value, ...saved }))}
      onChanged={() => {}}
    />
  );
}

test("claimLive tells a held claim from one that ran out or was never made", () => {
  assert.equal(claimLive(task()), false);
  assert.equal(claimLive(task({ claimedById: bot, claimExpiresAtUtc: inMinutes(30) })), true);
  assert.equal(claimLive(task({ claimedById: bot, claimExpiresAtUtc: minutes(30) })), false, "past its expiry until the sweep releases it");
  assert.equal(claimLive(task({ claimedById: bot, claimExpiresAtUtc: null })), true);
});

test("the claim line names who holds a claim and for how long, and the person takes it over, releases it, then can claim again", async () => {
  const held = task({ claimedById: bot, claimedAtUtc: minutes(12), claimExpiresAtUtc: inMinutes(108), agentReady: true });
  const { api, calls } = fakeClaims();
  const people: Record<string, PersonResponse> = { [bot]: person("Build bot", "agent"), [me]: person("You", "person", true) };
  const user = userEvent.setup({ document: dom.window.document });
  const { rerender } = render(<Harness initial={held} api={api} people={people} />, { container: dom.window.document.body });
  try {
    const section = await screen.findByTestId("agent-section");
    await within(section).findByText("Build bot");
    assert.match(section.textContent ?? "", /Claimed by Build bot · 12 minutes ago/);
    assert.equal(within(section).queryByRole("button", { name: "Claim" }), null, "a live claim by someone else cannot be claimed");
    await user.click(within(section).getByRole("button", { name: "Take over" }));
    await within(section).findByText("you");
    assert.deepEqual(withoutOperation(calls[0]), { method: "takeOver", taskId: "t1", expectedVersion: "4" });
    assert.equal(within(section).queryByRole("button", { name: "Take over" }), null);
    await user.click(within(section).getByRole("button", { name: "Release" }));
    await within(section).findByRole("button", { name: "Claim" });
    assert.deepEqual(withoutOperation(calls[1]), { method: "release", taskId: "t1", expectedVersion: "5" });
    assert.doesNotMatch(section.textContent ?? "", /Claimed by/);
    await user.click(within(section).getByRole("button", { name: "Claim" }));
    await within(section).findByText("you");
    assert.deepEqual(withoutOperation(calls[2]), { method: "claim", taskId: "t1", expectedVersion: "6" });
    assert.equal(within(section).queryByRole("button", { name: "Claim" }), null);

    // A finished task cannot be claimed, and a claim past its expiry reads as expired and can be taken again.
    rerender(<Harness key="done" initial={task({ status: "Done" })} api={api} people={people} />);
    assert.equal(within(await screen.findByTestId("agent-section")).queryByRole("button", { name: "Claim" }), null);
    rerender(
      <Harness
        key="expired"
        initial={task({ claimedById: bot, claimedAtUtc: minutes(200), claimExpiresAtUtc: minutes(80) })}
        api={api}
        people={people}
      />,
    );
    const expired = await screen.findByTestId("agent-section");
    await within(expired).findByText("Build bot");
    assert.match(expired.textContent ?? "", /Claim by Build bot expired/);
    assert.equal(within(expired).queryByRole("button", { name: "Take over" }), null);
    assert.ok(within(expired).getByRole("button", { name: "Claim" }));
  } finally {
    cleanup();
  }
});

test("Ready for agent goes through claims.setReady and, with several local agents, points the task at one", async () => {
  const { api, calls } = fakeClaims();
  const user = userEvent.setup({ document: dom.window.document });
  const { rerender } = render(
    <Harness
      initial={task()}
      api={api}
      people={{}}
      agents={[agentView("a1", "Build bot"), agentView("a2", "Deploy bot"), agentView("a3", "Elsewhere bot", "other-list")]}
    />,
    {
      container: dom.window.document.body,
    },
  );
  try {
    const section = await screen.findByTestId("agent-section");
    const ready = within(section).getByRole("switch", { name: "Ready for agent" }) as HTMLInputElement;
    assert.equal(ready.checked, false);
    assert.equal(within(section).queryByLabelText("Agent"), null, "no agent to point at while the task is not Ready");
    await user.click(ready);
    await waitFor(() =>
      assert.equal(
        (within(screen.getByTestId("agent-section")).getByRole("switch", { name: "Ready for agent" }) as HTMLInputElement).checked,
        true,
      ),
    );
    assert.deepEqual(withoutOperation(calls[0]), { method: "setReady", taskId: "t1", expectedVersion: "4", ready: true, agentId: null });
    const select = within(screen.getByTestId("agent-section")).getByLabelText("Agent") as HTMLSelectElement;
    assert.equal(select.value, "");
    // Only agents that can reach the task's list are offered: one without it could never claim the task.
    assert.deepEqual(
      [...select.options].map((option) => option.value),
      ["", "a1", "a2"],
    );
    await user.selectOptions(select, "a2");
    await waitFor(() =>
      assert.equal((within(screen.getByTestId("agent-section")).getByLabelText("Agent") as HTMLSelectElement).value, "a2"),
    );
    assert.deepEqual(withoutOperation(calls[1]), { method: "setReady", taskId: "t1", expectedVersion: "5", ready: true, agentId: "a2" });
    await user.selectOptions(within(screen.getByTestId("agent-section")).getByLabelText("Agent"), "");
    await waitFor(() => assert.equal((within(screen.getByTestId("agent-section")).getByLabelText("Agent") as HTMLSelectElement).value, ""));
    assert.deepEqual(withoutOperation(calls[2]), { method: "setReady", taskId: "t1", expectedVersion: "6", ready: true, agentId: null });
    await user.click(within(screen.getByTestId("agent-section")).getByRole("switch", { name: "Ready for agent" }));
    await waitFor(() =>
      assert.equal(
        (within(screen.getByTestId("agent-section")).getByRole("switch", { name: "Ready for agent" }) as HTMLInputElement).checked,
        false,
      ),
    );
    assert.deepEqual(withoutOperation(calls[3]), { method: "setReady", taskId: "t1", expectedVersion: "7", ready: false, agentId: null });

    // With one local agent there is nothing to choose, and a shared task is never pointed at a local agent.
    rerender(<Harness key="one" initial={task({ agentReady: true })} api={api} people={{}} agents={[agentView("a1", "Build bot")]} />);
    await screen.findByTestId("agent-section");
    assert.equal(within(screen.getByTestId("agent-section")).queryByLabelText("Agent"), null);
    rerender(
      <Harness
        key="shared"
        initial={task({ agentReady: true, spaceId: "shared" })}
        api={api}
        people={{}}
        agents={[agentView("a1", "Build bot"), agentView("a2", "Deploy bot")]}
        shared
      />,
    );
    await screen.findByTestId("agent-section");
    assert.equal(within(screen.getByTestId("agent-section")).queryByLabelText("Agent"), null);
    assert.ok(
      within(screen.getByTestId("agent-section")).getByRole("switch", { name: "Ready for agent" }),
      "a shared task can still be made Ready",
    );
  } finally {
    cleanup();
  }
});

test("a shared task's claim change is sent to the service and waits for sync; a refusal shows the service's words", async () => {
  const { api, calls } = fakeClaims({
    claims: { claim: { task: null }, release: { error: "Tasks in shared spaces are claimed through the service while online." } },
  });
  const people: Record<string, PersonResponse> = { [me]: person("You", "person", true) };
  const peopleApi = { get: async ({ actorId }: { actorId: string }) => ({ ok: true, person: people[actorId] ?? null, error: null }) };
  const agentsApi = { list: async () => ({ ok: true, agents: [] as AgentViewDto[], error: null }) };
  let changed = 0;
  const user = userEvent.setup({ document: dom.window.document });
  const { rerender } = render(
    <AgentSection
      task={task({ spaceId: "shared" })}
      canEdit
      shared
      claimApi={api}
      agentsApi={agentsApi}
      peopleApi={peopleApi}
      onTaskChanged={() => {}}
      onChanged={() => changed++}
    />,
    { container: dom.window.document.body },
  );
  try {
    await user.click(await screen.findByRole("button", { name: "Claim" }));
    await screen.findByText("Claimed. The task updates as it syncs.");
    assert.equal(calls[0].method, "claim");
    assert.equal(changed, 1);
    assert.doesNotMatch(screen.getByTestId("agent-section").textContent ?? "", /Claimed by/, "the task itself arrives with sync");

    rerender(
      <AgentSection
        task={task({
          spaceId: "shared",
          status: "InProgress",
          claimedById: me,
          claimedAtUtc: minutes(3),
          claimExpiresAtUtc: inMinutes(90),
        })}
        canEdit
        shared
        claimApi={api}
        agentsApi={agentsApi}
        peopleApi={peopleApi}
        onTaskChanged={() => {}}
        onChanged={() => changed++}
      />,
    );
    await user.click(await screen.findByRole("button", { name: "Release" }));
    assert.match((await screen.findByRole("alert")).textContent ?? "", /claimed through the service while online/);
    assert.equal(changed, 1, "a refused change changes nothing");
  } finally {
    cleanup();
  }
});

test("a viewer sees who holds the claim and nothing to change; an unclaimed task shows no agent section at all", async () => {
  const { api } = fakeClaims();
  const people: Record<string, PersonResponse> = { [bot]: person("Build bot", "agent") };
  const { rerender } = render(
    <Harness
      initial={task({ claimedById: bot, claimedAtUtc: minutes(1), claimExpiresAtUtc: inMinutes(100) })}
      api={api}
      people={people}
      canEdit={false}
    />,
    {
      container: dom.window.document.body,
    },
  );
  try {
    const section = await screen.findByTestId("agent-section");
    await within(section).findByText("Build bot");
    assert.match(section.textContent ?? "", /Claimed by Build bot/);
    assert.equal(within(section).queryByRole("switch"), null);
    assert.equal(within(section).queryByRole("button"), null);
    rerender(<Harness key="unclaimed" initial={task()} api={api} people={people} canEdit={false} />);
    await waitFor(() => assert.equal(screen.queryByTestId("agent-section"), null));
  } finally {
    cleanup();
  }
  render(<ClaimedLine task={task()} />, { container: dom.window.document.body });
  assert.equal(dom.window.document.body.textContent, "", "nothing for an unclaimed task");
  cleanup();
});
