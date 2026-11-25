import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import type { HistoryEntryResponse, PersonResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { actorLabel, describeEvent, diaryAuthorLabel } = await import("./TaskJournal");

const me = "11111111-1111-4111-8111-111111111111";
const bot = "11111111-1111-4111-8111-111111111113";
const person = (actorId: string, displayName: string, kind: "person" | "agent" | null, isMe = false): PersonResponse => ({
  actorId,
  displayName,
  initials: displayName[0],
  colorIndex: 0,
  kind,
  isMe,
  role: kind === "person" ? 2 : null,
  former: false,
});
const directory = [
  person(`person:${me}`, "Sara", "person", true),
  person(`agent:${bot}`, "Build bot", "agent"),
  person("person:b", "Bo Chen", "person"),
];
const event = (eventKind: string, changedFields: string[] = ["Claim"]): HistoryEntryResponse =>
  ({
    id: "e1",
    taskId: "t1",
    operationId: "op",
    effectiveAtUtc: "2026-11-25T09:00:00Z",
    eventKind,
    actor: "local",
    changedFields,
    before: null,
    after: null,
  }) as unknown as HistoryEntryResponse;

test("history lines name the claim and agent events in plain words", () => {
  assert.equal(describeEvent(event("task.claimed")), "Claimed");
  assert.equal(describeEvent(event("task.unclaimed")), "Released the claim");
  assert.equal(describeEvent(event("task.takeover")), "Took over");
  assert.equal(describeEvent(event("task.claim-extended")), "Kept the claim");
  assert.equal(describeEvent(event("task.claim-expired")), "Claim expired");
  assert.equal(describeEvent(event("task.ready-for-agent", ["AgentReady"])), "Marked Ready for agent");
  assert.equal(describeEvent(event("task.not-ready-for-agent", ["AgentReady"])), "No longer Ready for agent");
  assert.equal(describeEvent(event("task.agent-reverted")), "Undid the agent's changes");
  assert.equal(describeEvent(event("task.updated", ["Title", "Notes"])), "Edited: title, notes", "other events read as before");
});

test("an agent's changes are attributed as “Build bot (agent)”, by the space directory or the people lookup", () => {
  assert.equal(actorLabel(`agent:${bot}`, directory), "Build bot (agent)");
  assert.equal(actorLabel(`person:${me}`, directory), "You");
  assert.equal(actorLabel("person:b", directory), "Bo Chen");
  assert.equal(actorLabel("agent:gone", directory), "Unknown agent");
  assert.equal(actorLabel("person:gone", directory), "Unknown person");
  assert.equal(actorLabel("local"), "You");
  assert.equal(diaryAuthorLabel(bot, directory), "Build bot (agent)", "a claim that ran out posts its note as the agent");
  assert.equal(diaryAuthorLabel(me, directory), "You");
});
