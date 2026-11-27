import { test } from "node:test";
import assert from "node:assert/strict";
import type { TagResponse } from "#neoastra";
import { listTags, sidebarEntries, sidebarTagLimit, tagForest, visibleRows } from "./tagList";

function tag(path: string, extra: Partial<TagResponse> = {}): TagResponse {
  const parts = path.split("/");
  return {
    id: path,
    name: parts.at(-1)!,
    path,
    parentId: parts.length > 1 ? parts.slice(0, -1).join("/") : null,
    color: null,
    version: "1",
    depth: parts.length - 1,
    pinned: false,
    archived: false,
    ...extra,
  };
}

const counts = (entries: Record<string, number>) => new Map(Object.entries(entries));
const labels = (list: ReturnType<typeof sidebarEntries>) => list.map((item) => item.label);

test("the sidebar shows pinned tags first, then the busiest top-level tags with open tasks", () => {
  const tags = [tag("idle"), tag("bug"), tag("backend"), tag("web"), tag("ai", { pinned: true })];
  assert.deepEqual(labels(sidebarEntries(tags, counts({ bug: 23, backend: 11, web: 6 }), null)), ["ai", "bug", "backend", "web"]);
});

test("a nested tag sits under its parent, and a pinned nested tag also shows on its own under its full path", () => {
  const tags = [tag("platform"), tag("platform/ios"), tag("platform/android"), tag("home"), tag("home/garden", { pinned: true })];
  const shown = sidebarEntries(tags, counts({ platform: 5, "platform/ios": 3, "platform/android": 2, home: 1, "home/garden": 1 }), null);
  assert.deepEqual(labels(shown), ["home/garden", "platform", "home"]);
  assert.deepEqual(
    shown.find((item) => item.label === "platform")!.children.map((node) => node.tag.path),
    ["platform/ios", "platform/android"],
    "children come busiest first",
  );
});

test("the sidebar stops at the limit but never drops a pinned tag", () => {
  const many = Array.from({ length: 20 }, (_, index) => tag(`t${String(index).padStart(2, "0")}`));
  const busy = counts(Object.fromEntries(many.map((item, index) => [item.id, index + 1])));
  assert.equal(sidebarEntries(many, busy, null).length, sidebarTagLimit);
  const pinned = many.slice(0, 10).map((item) => ({ ...item, pinned: true }));
  assert.equal(sidebarEntries([...pinned, ...many.slice(10)], busy, null).length, 10, "all ten pinned tags show and nothing else");
});

test("archiving a tag takes its nested tags out of the sidebar, unless one of them is open", () => {
  const tags = [tag("old", { archived: true }), tag("old/child"), tag("bug")];
  const busy = counts({ old: 5, "old/child": 5, bug: 1 });
  assert.deepEqual(labels(sidebarEntries(tags, busy, null)), ["bug"]);
  assert.deepEqual(labels(sidebarEntries(tags, busy, "old/child")), ["bug", "old/child"]);
});

test("the open tag stays visible when it is a nested tag of a hidden one", () => {
  const tags = [tag("platform"), tag("platform/ios"), tag("idle")];
  assert.deepEqual(labels(sidebarEntries(tags, counts({ platform: 2 }), "platform/ios")), ["platform"], "its parent already shows it");
  assert.deepEqual(labels(sidebarEntries(tags, counts({}), "platform/ios")), ["platform/ios"], "nothing else shows it");
});

test("the forest nests tags and a collapsed tag hides its descendants", () => {
  const tags = [tag("b"), tag("a"), tag("a/x"), tag("a/x/deep"), tag("a/y")];
  const forest = tagForest(tags, counts({}), "name");
  assert.deepEqual(
    visibleRows(forest, new Set()).map((row) => `${row.depth}:${row.tag.path}`),
    ["0:a", "1:a/x", "2:a/x/deep", "1:a/y", "0:b"],
  );
  assert.deepEqual(
    visibleRows(forest, new Set(["a"])).map((row) => row.tag.path),
    ["a", "b"],
  );
  assert.equal(visibleRows(forest, new Set())[0].hasChildren, true);
  assert.equal(visibleRows(forest, new Set())[3].hasChildren, false);
});

test("a nested tag whose parent is missing becomes a root", () => {
  const forest = tagForest([tag("a/x")], counts({}), "name");
  assert.deepEqual(
    forest.map((node) => node.tag.path),
    ["a/x"],
  );
});

test("the tags page filters by path, sorts by name or usage and separates archived tags", () => {
  const tags = [tag("home/garden"), tag("bug"), tag("Backend"), tag("old", { archived: true })];
  const usage = counts({ bug: 23, Backend: 11, "home/garden": 2 });
  const list = (filter: string, sort: "name" | "usage", showArchived = false) =>
    listTags(tags, usage, { filter, sort, showArchived }).map((item) => item.path);
  assert.deepEqual(list("", "name"), ["Backend", "bug", "home/garden"]);
  assert.deepEqual(list("", "usage"), ["bug", "Backend", "home/garden"]);
  assert.deepEqual(list("gard", "name"), ["home/garden"]);
  assert.deepEqual(list("", "name", true), ["old"]);
});
