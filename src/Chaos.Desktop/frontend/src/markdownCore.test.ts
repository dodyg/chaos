import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { declarationsOf, diagramFailureMessage, isSafeExternalUrl, reapplyInlineStyles } from "./markdownCore";

test("only http, https and mailto links are safe", () => {
  for (const url of ["http://example.com", "https://example.com/a?b=1", "mailto:a@example.com"]) assert.equal(isSafeExternalUrl(url), true, url);
  for (const url of ["javascript:alert(1)", "data:text/html,hi", "file:///etc/passwd", "tel:123", "chaos:task/1", "not a url", ""]) assert.equal(isSafeExternalUrl(url), false, url);
});

test("style declarations split on top-level semicolons only and keep !important", () => {
  assert.deepEqual(declarationsOf("--_fg:var(--pele-fg,#1f1f1f);fill: url(\"data:image/svg+xml;utf8,<a/>\") ;stroke:red !important;;broken"), [
    ["--_fg", "var(--pele-fg,#1f1f1f)", ""],
    ["fill", "url(\"data:image/svg+xml;utf8,<a/>\")", ""],
    ["stroke", "red", "important"],
  ]);
});

test("reapplying inline styles sets each declaration on the element", () => {
  const dom = new JSDOM("<svg><g id='g' style='--_fg:var(--pele-fg,#1f1f1f);stroke:red !important'></g></svg>");
  reapplyInlineStyles(dom.window.document);
  const style = (dom.window.document.getElementById("g") as unknown as { style: CSSStyleDeclaration }).style;
  assert.equal(style.getPropertyValue("--_fg"), "var(--pele-fg,#1f1f1f)");
  assert.equal(style.getPropertyValue("stroke"), "red");
  assert.equal(style.getPropertyPriority("stroke"), "important");
});

test("a diagram failure shows the error's message or a generic one", () => {
  assert.equal(diagramFailureMessage(new Error("bad syntax")), "bad syntax");
  assert.equal(diagramFailureMessage("nope"), "Could not draw this diagram.");
});
