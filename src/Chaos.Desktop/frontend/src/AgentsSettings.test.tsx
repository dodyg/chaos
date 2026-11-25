import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import { agentRpc, type AgentViewDto, type ServerAgentDto, type SpaceResponse } from "#neoastra";
import type { AgentsApi } from "./AgentsSettings";

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
const { AgentsSettings } = await import("./AgentsSettings");
const { SpacesProvider } = await import("./Sharing");

const shared = "11111111-1111-4111-8111-111111111111";
const team: SpaceResponse = { spaceId: shared, name: "Design team", kind: "Shared", role: 1, memberCount: 3 };
const lists = [
  { containerId: "inbox", containerName: "Inbox", allowed: false },
  { containerId: "chaos", containerName: "Chaos", allowed: true },
];
const view = (id: string, name: string, patch: Partial<AgentViewDto> = {}): AgentViewDto => ({
  agent: { id, name, pausedAtUtc: null, version: "1" },
  lists: lists.map((list) => ({ ...list })),
  tokens: [],
  ...patch,
});
const bump = (entry: AgentViewDto): AgentViewDto => ({
  ...entry,
  agent: { ...entry.agent, version: String(Number(entry.agent.version) + 1) },
});

type Call = Record<string, unknown>;
/** An agents service in memory: the lists start as above, every change bumps the agent's version and is logged. */
function fakeAgents(options: { pauseError?: string } = {}) {
  const calls: Call[] = [];
  let local = [view("a1", "Build bot")];
  let server: ServerAgentDto[] = [
    { id: "s1", name: "Deploy bot", pausedAtUtc: null, memberships: [{ spaceId: shared, allowedContainerIds: null }] },
  ];
  const entry = (id: unknown) => local.find((item) => item.agent.id === id)!;
  const api = {
    ...agentRpc,
    list: async () => ({ ok: true, agents: local, error: null }),
    create: async (request: Call) => {
      calls.push({ method: "create", ...request });
      local = [...local, view("a2", String(request.name))];
      return { ok: true, agent: entry("a2").agent, error: null };
    },
    setAccess: async (request: Call) => {
      calls.push({ method: "setAccess", ...request });
      local = local.map((item) =>
        item.agent.id === request.agentId
          ? {
              ...bump(item),
              lists: item.lists.map((list) =>
                list.containerId === request.containerId ? { ...list, allowed: request.allowed === true } : list,
              ),
            }
          : item,
      );
      return { ok: true, agent: entry(request.agentId).agent, error: null };
    },
    setPaused: async (request: Call) => {
      calls.push({ method: "setPaused", ...request });
      if (options.pauseError)
        return {
          ok: false,
          agent: null,
          error: { kind: "OperationConflict", message: options.pauseError, fields: [], correlationId: "x" },
        };
      local = local.map((item) =>
        item.agent.id === request.agentId
          ? { ...bump(item), agent: { ...bump(item).agent, pausedAtUtc: request.paused ? "2026-11-25T09:00:00Z" : null } }
          : item,
      );
      return { ok: true, agent: entry(request.agentId).agent, error: null };
    },
    createToken: async (request: Call) => {
      calls.push({ method: "createToken", ...request });
      const token = { id: "tok1", name: String(request.name), createdAtUtc: "2026-11-25T09:00:00Z", lastUsedAtUtc: null };
      local = local.map((item) => (item.agent.id === request.agentId ? { ...bump(item), tokens: [...item.tokens, token] } : item));
      return { ok: true, token, secret: "chaos_agent_secret_1", error: null };
    },
    revokeToken: async (request: Call) => {
      calls.push({ method: "revokeToken", ...request });
      local = local.map((item) =>
        item.agent.id === request.agentId ? { ...bump(item), tokens: item.tokens.filter((token) => token.id !== request.tokenId) } : item,
      );
      return { ok: true, agent: entry(request.agentId).agent, error: null };
    },
    undo: async (request: Call) => {
      calls.push({ method: "undo", ...request });
      return { ok: true, undone: 3, stoppedAt: null, conflict: null, error: null };
    },
    serverList: async () => ({ ok: true, agents: server, error: null }),
    revert: async (request: Call) => {
      calls.push({ method: "revert", ...request });
      return { ok: true, restoredGroups: 4, trashed: 1, skipped: [], error: null };
    },
  };
  return { api: api as unknown as AgentsApi, calls, setServer: (next: ServerAgentDto[]) => (server = next) };
}

const withoutOperation = (call: Call) => Object.fromEntries(Object.entries(call).filter(([key]) => key !== "operationId"));

test("a local agent is added, ticked for a Personal list and paused through the agents service, each with its version", async () => {
  const { api, calls } = fakeAgents();
  let changed = 0;
  const user = userEvent.setup({ document: dom.window.document });
  render(<AgentsSettings api={api} onChanged={() => changed++} />, { container: dom.window.document.body });
  try {
    const bot = await screen.findByRole("region", { name: "Agent Build bot" });
    assert.equal((within(bot).getByRole("checkbox", { name: "Chaos" }) as HTMLInputElement).checked, true);
    assert.equal((within(bot).getByRole("checkbox", { name: "Inbox" }) as HTMLInputElement).checked, false);

    await user.type(screen.getByRole("textbox", { name: "New agent name" }), "Deploy bot");
    await user.click(screen.getByRole("button", { name: "Add agent" }));
    await screen.findByRole("region", { name: "Agent Deploy bot" });
    assert.deepEqual(withoutOperation(calls[0]), { method: "create", name: "Deploy bot" });

    await user.click(within(bot).getByRole("checkbox", { name: "Inbox" }));
    await waitFor(() =>
      assert.equal(
        (within(screen.getByRole("region", { name: "Agent Build bot" })).getByRole("checkbox", { name: "Inbox" }) as HTMLInputElement)
          .checked,
        true,
      ),
    );
    assert.deepEqual(withoutOperation(calls[1]), { method: "setAccess", agentId: "a1", containerId: "inbox", allowed: true });

    await user.click(within(screen.getByRole("region", { name: "Agent Build bot" })).getByRole("button", { name: "Pause" }));
    await within(screen.getByRole("region", { name: "Agent Build bot" })).findByText("Paused");
    assert.deepEqual(withoutOperation(calls[2]), { method: "setPaused", agentId: "a1", expectedVersion: "2", paused: true });
    assert.ok(within(screen.getByRole("region", { name: "Agent Build bot" })).getByRole("button", { name: "Resume" }));
    assert.equal(changed, 3);
    assert.match(screen.getByText(/Agents reach Chaos through an MCP client/).textContent ?? "", /Personal lists you tick/);
  } finally {
    cleanup();
  }
});

test("a token's secret is shown once with a copy button, then only the token's name stays until it is revoked", async () => {
  const { api, calls } = fakeAgents();
  const user = userEvent.setup({ document: dom.window.document });
  render(<AgentsSettings api={api} />, { container: dom.window.document.body });
  try {
    const bot = await screen.findByRole("region", { name: "Agent Build bot" });
    assert.ok(within(bot).getByText(/No tokens/));
    await user.click(within(bot).getByRole("button", { name: "New token" }));
    await user.type(within(bot).getByRole("textbox", { name: "Token name" }), "Laptop");
    await user.click(within(bot).getByRole("button", { name: "Create token" }));
    const dialog = await screen.findByRole("dialog", { name: "Token for Build bot · Laptop" });
    assert.ok(within(dialog).getByText("chaos_agent_secret_1"));
    assert.match(within(dialog).getByRole("status").textContent ?? "", /shown only once[\s\S]*Q18[\s\S]*CHAOS_AGENT_TOKEN/);
    assert.deepEqual(withoutOperation(calls[0]), { method: "createToken", agentId: "a1", expectedVersion: "1", name: "Laptop" });
    // user-event stands in for the clipboard; the copy is read back from it.
    await user.click(within(dialog).getByRole("button", { name: "Copy token" }));
    await within(dialog).findByText("Copied");
    assert.equal(await dom.window.navigator.clipboard.readText(), "chaos_agent_secret_1");
    await user.click(within(dialog).getByRole("button", { name: "Done" }));
    assert.equal(screen.queryByText("chaos_agent_secret_1"), null);
    const token = within(screen.getByRole("region", { name: "Agent Build bot" })).getByText("Laptop");
    assert.ok(token);
    assert.match(screen.getByText(/used |never used/).textContent ?? "", /never used/);
    await user.click(screen.getByRole("button", { name: "Revoke Laptop" }));
    await waitFor(() => assert.equal(screen.queryByText("Laptop"), null));
    assert.deepEqual(withoutOperation(calls[1]), { method: "revokeToken", agentId: "a1", tokenId: "tok1" });
  } finally {
    cleanup();
  }
});

test("Undo recent changes asks first and reports how many changes it undid; a refused pause shows the service's message", async () => {
  const { api, calls } = fakeAgents({ pauseError: "The agent changed elsewhere while you were working. Refresh and try again." });
  const user = userEvent.setup({ document: dom.window.document });
  render(<AgentsSettings api={api} />, { container: dom.window.document.body });
  try {
    const bot = await screen.findByRole("region", { name: "Agent Build bot" });
    await user.click(within(bot).getByRole("button", { name: "Undo recent changes by Build bot" }));
    const confirm = await screen.findByRole("alertdialog", { name: "Undo the agent's changes?" });
    assert.equal(calls.length, 0);
    await user.click(within(confirm).getByRole("button", { name: "Undo changes" }));
    await screen.findByText("Undid 3 changes by Build bot.");
    assert.deepEqual(withoutOperation(calls[0]), { method: "undo", actorId: "agent:a1", maxOperations: null });

    await user.click(within(screen.getByRole("region", { name: "Agent Build bot" })).getByRole("button", { name: "Pause" }));
    assert.match((await screen.findByRole("alert")).textContent ?? "", /changed elsewhere while you were working/);
  } finally {
    cleanup();
  }
});

test("server agents show their spaces by name and revert the last hours of one space on request", async () => {
  const { api, calls } = fakeAgents();
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <SpacesProvider spaces={[team]}>
      <AgentsSettings api={api} />
    </SpacesProvider>,
    { container: dom.window.document.body },
  );
  try {
    const deploy = await screen.findByRole("region", { name: "Server agent Deploy bot" });
    assert.ok(within(deploy).getByText("Design team"));
    const form = within(deploy).getByRole("form", { name: "Revert Deploy bot in Design team" });
    assert.equal((within(form).getByRole("spinbutton") as HTMLInputElement).value, "24");
    await user.clear(within(form).getByRole("spinbutton"));
    await user.type(within(form).getByRole("spinbutton"), "6");
    await user.click(within(form).getByRole("button", { name: "Revert" }));
    await within(deploy).findByText("Restored 4 fields, trashed 1 task.");
    assert.deepEqual(withoutOperation(calls[0]), { method: "revert", agentId: "s1", spaceId: shared, hours: 6 });
  } finally {
    cleanup();
  }
});
