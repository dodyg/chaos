import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import { MarkdownNotes } from "./MarkdownNotes.jsx";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "https://example.test/" });
Object.assign(globalThis, {
  window: dom.window,
  document: dom.window.document,
  HTMLElement: dom.window.HTMLElement,
  SVGElement: dom.window.SVGElement,
  MutationObserver: dom.window.MutationObserver,
  getComputedStyle: dom.window.getComputedStyle.bind(dom.window),
  IS_REACT_ACT_ENVIRONMENT: true,
});
dom.window.HTMLCanvasElement.prototype.getContext = () => null;

async function show(source) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  await act(async () => {
    root.render(<MarkdownNotes>{source}</MarkdownNotes>);
  });
  for (let i = 0; i < 100 && !container.querySelector(".mermaid-diagram svg, .mermaid-block [role=status]"); i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 10));
    });
  }
  return {
    container,
    close: async () => {
      await act(async () => root.unmount());
      container.remove();
    },
  };
}

test("member mentions are inert chips and unknown schemes stay blocked", async () => {
  const id = "0f8fad5b-d9cb-469f-a165-70867728950e";
  const view = await show(`[@Mina](chaos:member/${id}) [other](chaos:unknown/${id})`);
  try {
    assert.equal(view.container.querySelector('[data-mention="true"]').textContent, "@Mina");
    assert.equal(view.container.querySelectorAll('[data-mention="true"]').length, 1);
    assert.equal(view.container.querySelector("a"), null);
    assert.match(view.container.textContent, /other/);
  } finally {
    await view.close();
  }
});

test("Mermaid fences render SVG while ordinary code remains code", async () => {
  const view = await show("```mermaid\nerDiagram\n CUSTOMER ||--o{ ORDER : places\n```\n\n```js\nconst n = 1;\n```");
  try {
    const svg = view.container.querySelector(".mermaid-diagram svg");
    assert.ok(svg);
    assert.equal(svg.style.getPropertyValue("--_fg"), "var(--pele-fg,#1f1f1f)");
    assert.equal(svg.style.getPropertyValue("--_s"), "var(--pele-surface,#f3f3f3)");
    assert.match(view.container.querySelector("pre code").textContent, /const n = 1/);
  } finally {
    await view.close();
  }
});

test("invalid Mermaid displays its source safely", async () => {
  const view = await show("```mermaid\nnot a diagram <script>alert(1)</script>\n```");
  try {
    assert.match(view.container.querySelector("[role=status]").textContent, /Could not draw/);
    assert.match(view.container.querySelector("pre code").textContent, /not a diagram/);
    assert.equal(view.container.querySelector("script"), null);
  } finally {
    await view.close();
  }
});

test("only http, https, mailto and same-note links are clickable, as in the desktop app", async () => {
  const view = await show(
    "[web](https://example.com) [mail](mailto:a@example.com) [call](tel:123) [js](javascript:alert(1)) [local](#top)\n\n# top",
  );
  try {
    const hrefs = [...view.container.querySelectorAll("a")].map((link) => link.getAttribute("href"));
    assert.deepEqual(hrefs, ["https://example.com", "mailto:a@example.com", "#top"]);
    assert.match(view.container.textContent, /call/);
  } finally {
    await view.close();
  }
});

test("attachment images load from the web endpoint and other images stay blocked", async () => {
  const id = "79ABD8C2-7F78-434C-B973-16B9DFBF1B09";
  const view = await show(`![shot](chaos:attachment/${id})\n\n![remote](https://evil.test/x.png)`);
  try {
    const pictures = [...view.container.querySelectorAll("img")];
    assert.equal(pictures.length, 1);
    assert.equal(pictures[0].getAttribute("src"), `/v1/web/attachments/${id.toLowerCase()}`);
    assert.equal(pictures[0].getAttribute("alt"), "shot");
    assert.match(view.container.querySelector(".note-image").textContent, /remote/);
  } finally {
    await view.close();
  }
});
