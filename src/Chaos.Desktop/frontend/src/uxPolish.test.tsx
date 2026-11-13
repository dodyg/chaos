import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  HTMLInputElement: { value: dom.window.HTMLInputElement, configurable: true },
  Element: { value: dom.window.Element, configurable: true },
  Node: { value: dom.window.Node, configurable: true },
  DOMRect: { value: dom.window.DOMRect, configurable: true },
  CustomEvent: { value: dom.window.CustomEvent, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const { daysBetween, dueTone, friendlyDate, relativeDays } = await import("./dates");
const { CommandPalette, matchesQuery } = await import("./CommandPalette");
const { handleListKeys } = await import("./listKeyboard");

test("dates read as people say them and due dates carry urgency", () => {
  const today = "2026-11-13"; // a Friday
  assert.equal(friendlyDate("2026-11-13", today), "Today");
  assert.equal(friendlyDate("2026-11-14", today), "Tomorrow");
  assert.equal(friendlyDate("2026-11-12", today), "Yesterday");
  assert.equal(friendlyDate("2026-11-16", today), new Intl.DateTimeFormat(undefined, { weekday: "short", timeZone: "UTC" }).format(new Date("2026-11-16T00:00:00Z")));
  assert.ok(!friendlyDate("2026-11-30", today).includes("2026"), "this year's dates leave the year out");
  assert.ok(friendlyDate("2027-01-05", today).includes("2027"));
  assert.equal(daysBetween("2026-11-13", "2026-11-30"), 17);
  assert.equal(relativeDays("2026-11-30", today), "in 17 days");
  assert.equal(relativeDays("2026-11-10", today), "3 days ago");
  assert.equal(dueTone("2026-11-12", today), "overdue");
  assert.equal(dueTone("2026-11-14", today), "soon");
  assert.equal(dueTone("2026-11-20", today), "normal");
  assert.equal(dueTone("2026-11-12", today, "planned"), "normal", "a past planned date is not overdue");
});

test("palette matching needs every typed word, in any order", () => {
  const sprint = { label: "Sprint 21", group: "Projects", keywords: "Project" };
  assert.ok(matchesQuery(sprint, "spr"));
  assert.ok(matchesQuery(sprint, "project 21"));
  assert.ok(!matchesQuery(sprint, "sprint home"));
});

test("the command palette filters, moves with the arrows, runs on Enter and finds tasks", async () => {
  const ran: string[] = [];
  const opened: string[] = [];
  let closed = 0;
  const commands = [
    { id: "a", group: "Go to", label: "My Day", run: () => ran.push("My Day") },
    { id: "b", group: "Go to", label: "My Week", run: () => ran.push("My Week") },
    { id: "c", group: "Lists", label: "Home", run: () => ran.push("Home") },
  ];
  render(<CommandPalette commands={commands} searchTasks={async text => [{ id: "t1", title: `Found ${text}`, detail: "Inbox" }]} onOpenTask={id => opened.push(id)} onClose={() => closed++}/>, { container: dom.window.document.body });
  const box = screen.getByRole("combobox", { name: "Search commands, lists and tasks" });
  fireEvent.change(box, { target: { value: "my" } });
  assert.equal(screen.getAllByRole("option").filter(option => option.textContent?.startsWith("My")).length, 2);
  fireEvent.keyDown(box, { key: "ArrowDown" });
  fireEvent.keyDown(box, { key: "Enter" });
  assert.deepEqual(ran, ["My Week"]);
  assert.equal(closed, 1);

  fireEvent.change(box, { target: { value: "milk" } });
  await waitFor(() => assert.ok(screen.getByRole("option", { name: /Found milk/ })));
  fireEvent.click(screen.getByRole("option", { name: /Found milk/ }));
  assert.deepEqual(opened, ["t1"]);
  fireEvent.keyDown(box, { key: "Escape" });
  assert.equal(closed, 3);
  cleanup();
});

test("J/K move between task rows, X toggles done, M adds to My Day and folded rows are skipped", () => {
  const toggled: string[] = [];
  const added: string[] = [];
  function Rows() {
    return <div onKeyDown={event => handleListKeys(event, id => added.push(id))}>
      {["a", "b"].map(id => <div key={id} data-task-row={id}><input type="checkbox" aria-label={`Done ${id}`} onChange={() => toggled.push(id)}/><button type="button" data-slot="row-title">Task {id}</button></div>)}
      <details><summary>Finished</summary><div data-task-row="c"><button type="button" data-slot="row-title">Task c</button></div></details>
    </div>;
  }
  render(<Rows/>, { container: dom.window.document.body });
  const first = screen.getByRole("button", { name: "Task a" });
  first.focus();
  fireEvent.keyDown(first, { key: "j" });
  assert.equal(dom.window.document.activeElement?.textContent, "Task b");
  fireEvent.keyDown(dom.window.document.activeElement!, { key: "j" });
  assert.equal(dom.window.document.activeElement?.textContent, "Task b", "the folded Finished row is not reached");
  fireEvent.keyDown(dom.window.document.activeElement!, { key: "x" });
  fireEvent.keyDown(dom.window.document.activeElement!, { key: "m" });
  fireEvent.keyDown(dom.window.document.activeElement!, { key: "k" });
  assert.equal(dom.window.document.activeElement?.textContent, "Task a");
  assert.deepEqual(toggled, ["b"]);
  assert.deepEqual(added, ["b"]);
  cleanup();
});
