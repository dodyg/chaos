import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import type { CollaborationNotice } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, cleanup } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { CollaborationInbox } = await import("./CollaborationInbox");

test("the inbox opens a notice and marks it read", async () => {
  let notice: CollaborationNotice = {
    id: "notice",
    kind: "mentioned",
    spaceId: "space",
    entityKind: "diary_entry",
    entityId: "entry",
    actorId: "actor",
    actorName: "Bo",
    createdAtUtc: "2026-11-22T10:00:00Z",
    notifyAtUtc: "2026-11-22T10:00:00Z",
    deliveredAtUtc: "2026-11-22T10:00:00Z",
    readAtUtc: null,
    taskId: "task",
  };
  const api = {
    list: async () => ({ ok: true, notices: [notice], error: null }),
    read: async () => {
      notice = { ...notice, readAtUtc: "2026-11-22T10:01:00Z" };
      return { ok: true, error: null };
    },
  };
  let opened: CollaborationNotice | null = null;
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <CollaborationInbox
      api={api}
      onOpen={(value) => {
        opened = value;
      }}
    />,
  );
  try {
    assert.ok(await screen.findByText(/mentioned you/));
    await user.click(screen.getByRole("button", { name: "Open" }));
    assert.equal((opened as CollaborationNotice | null)?.taskId, "task");
    assert.ok(await screen.findByText(/Read/));
  } finally {
    cleanup();
  }
});
