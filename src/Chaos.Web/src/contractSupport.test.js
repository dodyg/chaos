import { test } from "node:test";
import assert from "node:assert/strict";
import { assertServerShape, serverContract } from "./contractSupport.js";

const id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

test("a fixture shaped like the server's record passes", () => {
  assertServerShape("WebSearchResult", { truncated: false, items: [{ kind: "task", id, title: "Task", detail: null, snippet: null }] });
});

test("a renamed, missing or mistyped field is reported", () => {
  assert.throws(
    () => assertServerShape("WebSearchResult", { truncated: false, items: [{ kind: "task", id, name: "Task" }] }),
    /items\[0\]\.name is not a field of WebSearchItem[\s\S]*items\[0\]\.title is missing/,
  );
  assert.throws(() => assertServerShape("WebSearchResult", { truncated: "no", items: [] }), /truncated is not a boolean/);
  assert.throws(
    () => assertServerShape("WebSearchResult", { truncated: false, items: [{ kind: "task", id: "x", title: "t" }] }),
    /id is not a guid/,
  );
});

test("the snapshot covers the records the PWA reads", () => {
  for (const name of ["WebToday", "WebTask", "WebList", "WebSearchResult", "WebPage", "WebNote", "WebAppearance"])
    assert.ok(serverContract[name], name);
});
