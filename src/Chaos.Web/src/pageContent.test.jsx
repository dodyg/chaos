import { test } from "node:test";
import assert from "node:assert/strict";
import { pageContent } from "./pageContent.js";

test("page front matter extracts the scene and keeps only the Markdown body", () => {
  assert.deepEqual(pageContent('---\r\nbackground: "Tide" # scene\r\ntags: [one, two]\r\n---\r\n\r\n# Hello'), {
    background: "Tide",
    body: "\r\n# Hello",
  });
});

test("pages without valid front matter retain all of their text", () => {
  for (const text of ["# Ordinary page", "---\nbackground: tide\nno closing delimiter", "---\nbackground: [tide\n---\nBody"]) {
    assert.deepEqual(pageContent(text), { background: null, body: text });
  }
});

test("unknown properties do not suppress a valid scene", () => {
  assert.deepEqual(pageContent("---\nbackground: tide\nicon: star\n---\nBody"), { background: "tide", body: "Body" });
});
