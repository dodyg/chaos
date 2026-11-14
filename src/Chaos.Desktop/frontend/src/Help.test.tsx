import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, taskRpc } from "#neoastra";

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
const { render, screen, cleanup, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { Help, topicMatches } = await import("./Help");
const { App } = await import("./App");

test("every query word must appear in a help topic", () => {
  assert.equal(topicMatches({ title: "Keyboard shortcuts", keywords: "keys" }, "Ctrl+N new task", "ctrl keyboard"), true);
  assert.equal(topicMatches({ title: "Keyboard shortcuts", keywords: "keys" }, "Ctrl+N new task", "ctrl backup"), false);
});

test("help search narrows the topics and its links go to the matching page", async () => {
  const visited: string[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(<Help onNavigate={view => visited.push(view)}/>, { container: dom.window.document.body });
  assert.ok(screen.getByRole("heading", { name: "Quick add in plain language" }));
  assert.ok(screen.getByRole("navigation", { name: "Help topics" }));

  await user.type(screen.getByLabelText("Search help"), "backup");
  assert.equal(screen.queryByRole("heading", { name: "Quick add in plain language" }), null);
  await user.click(screen.getByRole("button", { name: /Open Data & backups/ }));
  assert.deepEqual(visited, ["data"]);

  await user.clear(screen.getByLabelText("Search help"));
  await user.type(screen.getByLabelText("Search help"), "zzzz");
  assert.ok(screen.getByText(/No help topic mentions/));
  cleanup();
});

test("F1 and the sidebar Help button open the help page", async () => {
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }) },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" api={api} templates={<></>}/>, { container: dom.window.document.body });
  fireEvent.keyDown(dom.window.document.body, { key: "F1" });
  assert.ok(await screen.findByRole("heading", { name: "Using Chaos" }));

  await user.click(screen.getByRole("button", { name: "Open Templates" }));
  assert.equal(screen.queryByRole("heading", { name: "Using Chaos" }), null);
  await user.click(screen.getByRole("button", { name: /^Help/ }));
  assert.ok(await screen.findByRole("heading", { name: "Using Chaos" }));
  cleanup();
});
