import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { TagResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  Element: { value: dom.window.Element, configurable: true },
  Node: { value: dom.window.Node, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { TagsPage } = await import("./TagsPage");

function tag(path: string, extra: Partial<TagResponse> = {}): TagResponse {
  return { id: path, name: path, path, parentId: null, color: null, version: "3", depth: 0, pinned: false, archived: false, ...extra };
}

test("the tags page pins, archives and filters tags through the tag RPC", async () => {
  const pins: unknown[] = [];
  const archives: unknown[] = [];
  let changed = 0;
  const user = userEvent.setup({ document: dom.window.document });
  const api = {
    setPinned: async (request: unknown) => {
      pins.push(request);
      return { ok: true, tag: null, error: null };
    },
    setArchived: async (request: unknown) => {
      archives.push(request);
      return { ok: true, tag: null, error: null };
    },
  };
  render(
    <TagsPage
      tags={[tag("backend"), tag("bug", { pinned: true }), tag("old", { archived: true })]}
      counts={new Map([["bug", 23]])}
      api={api}
      onOpen={() => {}}
      onManage={() => {}}
      onChanged={async () => {
        changed += 1;
      }}
    />,
    { container: dom.window.document.body },
  );

  const list = screen.getByRole("list", { name: "Tags" });
  assert.equal(within(list).getAllByRole("listitem").length, 2, "archived tags are hidden until asked for");
  assert.ok(within(list).getByText("23 open"));

  await user.click(screen.getByRole("button", { name: "Pin backend to the sidebar" }));
  await waitFor(() => assert.equal(changed, 1));
  assert.deepEqual(pins, [
    { operationId: (pins[0] as { operationId: string }).operationId, tagId: "backend", expectedVersion: "3", pinned: true },
  ]);

  await user.click(screen.getByRole("button", { name: "Unpin bug" }));
  await waitFor(() => assert.equal((pins[1] as { pinned: boolean }).pinned, false));

  await user.click(screen.getByRole("button", { name: "Archive backend" }));
  await waitFor(() => assert.equal((archives[0] as { archived: boolean }).archived, true));

  await user.type(screen.getByRole("textbox", { name: "Filter tags" }), "zzz");
  assert.ok(await screen.findByText("No tag matches."));

  await user.clear(screen.getByRole("textbox", { name: "Filter tags" }));
  await user.click(screen.getByRole("button", { name: /^Archived \(1\)/ }));
  const archived = await screen.findByRole("list", { name: "Archived tags" });
  assert.ok(within(archived).getByRole("button", { name: "Restore old" }));
  assert.equal(within(archived).queryByRole("button", { name: /^Pin / }), null, "an archived tag cannot be pinned");
  cleanup();
});
