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
});

const { render, screen, cleanup, fireEvent } = await import("@testing-library/react");
const { DeletedItems } = await import("./components/common");

const many = Array.from({ length: 120 }, (_, i) => ({ id: `n${i}`, title: i === 99 ? "Quarterly budget" : `Note ${i}` }));

test("a long trash draws a bounded list and finds the rest by name", () => {
  const restored: string[] = [];
  render(<DeletedItems noun="notes" items={many} name={item => item.title} busy={false} onRestore={item => restored.push(item.id)}/>);
  assert.equal(screen.getAllByRole("button", { name: "Restore" }).length, 50);
  assert.ok(screen.getByText("Showing 50 of 120. Type to find the others."));

  fireEvent.change(screen.getByRole("searchbox", { name: "Find deleted notes" }), { target: { value: "budget" } });
  const buttons = screen.getAllByRole("button", { name: "Restore" });
  assert.equal(buttons.length, 1);
  assert.ok(screen.getByText("1 of 120"));
  fireEvent.click(buttons[0]);
  assert.deepEqual(restored, ["n99"]);

  fireEvent.change(screen.getByRole("searchbox", { name: "Find deleted notes" }), { target: { value: "nothing like this" } });
  assert.equal(screen.queryAllByRole("button", { name: "Restore" }).length, 0);
  cleanup();
});

test("a short trash has no find box", () => {
  render(<DeletedItems noun="notes" items={many.slice(0, 5)} name={item => item.title} busy={false} onRestore={() => {}}/>);
  assert.equal(screen.queryByRole("searchbox"), null);
  assert.equal(screen.getAllByRole("button", { name: "Restore" }).length, 5);
  cleanup();
});
