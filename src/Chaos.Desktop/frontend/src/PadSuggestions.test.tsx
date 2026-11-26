import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { proposalRpc, type PadItemResponse, type ProposalDto } from "#neoastra";

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
const { PadSuggestions } = await import("./PadSuggestions");

const note = { id: "pad-1", markdown: "Call the venue\nOrder the cake", kind: "text" } as PadItemResponse;
const suggestion = (id: string, title: string): ProposalDto => ({
  id,
  version: "1",
  padItemId: "pad-1",
  agentId: "bot",
  agentName: "Build bot",
  title,
  notes: "Ask about Friday",
  quickAddText: "Call the venue tomorrow",
  createdAtUtc: "2026-11-26T09:00:00Z",
});

/** A proposals API over an in-memory list; accepting and dismissing remove the row like the service does. */
function fakeApi(rows: ProposalDto[]) {
  const accepted: { proposalId: string; containerId: string | null }[] = [];
  const dismissed: string[] = [];
  const api = {
    ...proposalRpc,
    list: async () => ({ ok: true, items: [...rows], error: null }),
    accept: async (request: { proposalId: string; containerId: string | null }) => {
      accepted.push({ proposalId: request.proposalId, containerId: request.containerId });
      rows.splice(0, rows.length, ...rows.filter((row) => row.id !== request.proposalId));
      return { ok: true, task: null, replayed: false, error: null };
    },
    dismiss: async (request: { proposalId: string }) => {
      dismissed.push(request.proposalId);
      rows.splice(0, rows.length, ...rows.filter((row) => row.id !== request.proposalId));
      return { ok: true, replayed: false, error: null };
    },
  } as unknown as typeof proposalRpc;
  return { api, accepted, dismissed };
}

const lists = [{ id: "inbox", name: "Inbox", kind: "Inbox", archived: false, version: "1", deadlineDate: null, defaultCategory: null }];
const noop = () => undefined;

test("a suggestion names the agent and its note, and Accept makes it a task through the service", async () => {
  const { api, accepted } = fakeApi([suggestion("p1", "Call the venue"), suggestion("p2", "Order the cake")]);
  let changed = 0;
  render(
    <PadSuggestions
      api={api}
      notes={[note]}
      noteLabel={() => "Call the venue"}
      containers={lists}
      revision={0}
      onChanged={() => (changed += 1)}
      onError={noop}
    />,
  );
  const first = await screen.findByLabelText("Suggestion: Call the venue");
  assert.match(first.textContent ?? "", /Suggested by Build bot/);
  assert.match(first.textContent ?? "", /from “Call the venue”/);
  assert.match(first.textContent ?? "", /Quick add: Call the venue tomorrow/);

  await userEvent.click(first.querySelector("button") as HTMLElement);
  await waitFor(() => assert.equal(screen.queryByLabelText("Suggestion: Call the venue"), null));
  assert.deepEqual(accepted, [{ proposalId: "p1", containerId: null }]);
  assert.equal(changed, 1);
  assert.ok(screen.getByLabelText("Suggestion: Order the cake")); // the other one waits
  cleanup();
});

test("Dismiss closes a suggestion without accepting it, and an empty list shows nothing", async () => {
  const { api, accepted, dismissed } = fakeApi([suggestion("p1", "Call the venue")]);
  render(<PadSuggestions api={api} notes={[]} noteLabel={() => ""} containers={lists} revision={0} onChanged={noop} onError={noop} />);
  await screen.findByLabelText("Suggestion: Call the venue");
  await userEvent.click(screen.getByRole("button", { name: /Dismiss/ }));
  await waitFor(() => assert.equal(screen.queryByLabelText("Suggested tasks"), null));
  assert.deepEqual(dismissed, ["p1"]);
  assert.deepEqual(accepted, []);
  cleanup();
});

test("a refused Accept is reported and keeps the suggestion", async () => {
  const rows = [suggestion("p1", "Call the venue")];
  const api = {
    ...proposalRpc,
    list: async () => ({ ok: true, items: rows, error: null }),
    accept: async () => ({
      ok: false,
      task: null,
      replayed: false,
      error: { message: "That suggestion changed elsewhere. Refresh and try again.", fields: [] },
    }),
  } as unknown as typeof proposalRpc;
  const errors: (string | null)[] = [];
  render(
    <PadSuggestions
      api={api}
      notes={[]}
      noteLabel={() => ""}
      containers={lists}
      revision={0}
      onChanged={noop}
      onError={(m) => errors.push(m)}
    />,
  );
  await userEvent.click(await screen.findByRole("button", { name: /Accept/ }));
  await waitFor(() => assert.ok(errors.includes("That suggestion changed elsewhere. Refresh and try again.")));
  assert.ok(screen.getByLabelText("Suggestion: Call the venue"));
  cleanup();
});

test("suggestions that cannot load leave the pad alone", async () => {
  const api = { ...proposalRpc, list: async () => Promise.reject(new Error("offline")) } as unknown as typeof proposalRpc;
  const { container } = render(
    <PadSuggestions api={api} notes={[]} noteLabel={() => ""} containers={lists} revision={0} onChanged={noop} onError={noop} />,
  );
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(container.textContent, "");
  cleanup();
});
