import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import { assertServerShape } from "./contractSupport.js";


globalThis.BroadcastChannel = undefined;
const { SearchScreen } = await import("./SearchScreen.jsx");
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "https://example.test/app/#search" });
Object.assign(globalThis, { window: dom.window, document: dom.window.document, HTMLElement: dom.window.HTMLElement,
  history: dom.window.history, IS_REACT_ACT_ENVIRONMENT: true });

async function render(props) {
  const element = document.createElement("div");
  document.body.append(element);
  const root = createRoot(element);
  await act(async () => { root.render(<SearchScreen {...props} />); });
  return { element, close: async () => { await act(async () => root.unmount()); element.remove(); } };
}

test("groups results and links back to the search", async () => {
  const result = { truncated: false, items: [
    { kind: "task", id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", title: "Task", detail: "Inbox", snippet: null },
    { kind: "page", id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", title: "Page", detail: "Page", snippet: "match" },
  ] };
  assertServerShape("WebSearchResult", result);
  const search = async () => result;
  const view = await render({ initialQuery: "hello & world", session: { deviceId: "device" }, offline: false, search });
  try {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 350)); });
    assert.match(view.element.textContent, /2 results/);
    assert.match(view.element.textContent, /Tasks/);
    assert.match(view.element.textContent, /Pages/);
    const links = [...view.element.querySelectorAll(".rows a")];
    assert.equal(links.length, 2);
    assert.match(decodeURIComponent(links[0].getAttribute("href")), /from=#search\?q=hello%20%26%20world/);
  } finally { await view.close(); }
});

test("offline search explains that a connection is needed", async () => {
  const view = await render({ initialQuery: "hello", session: null, offline: true });
  try { assert.match(view.element.textContent, /Connect to search/); }
  finally { await view.close(); }
});
