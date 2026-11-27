import { test } from "node:test";
import assert from "node:assert/strict";
import type { TagResponse } from "#neoastra";
import { listTags, sidebarTagLimit, sidebarTags } from "./tagList";

function tag(path: string, extra: Partial<TagResponse> = {}): TagResponse {
  return {
    id: path,
    name: path.split("/").at(-1)!,
    path,
    parentId: null,
    color: null,
    version: "1",
    depth: path.split("/").length - 1,
    pinned: false,
    archived: false,
    ...extra,
  };
}

const counts = (entries: Record<string, number>) => new Map(Object.entries(entries));

test("the sidebar shows pinned tags first, then the busiest, and hides tags with no open tasks", () => {
  const tags = [tag("idle"), tag("bug"), tag("backend"), tag("web"), tag("ai", { pinned: true })];
  const shown = sidebarTags(tags, counts({ bug: 23, backend: 11, web: 6 }), null).map((item) => item.path);
  assert.deepEqual(shown, ["ai", "bug", "backend", "web"]);
});

test("the sidebar stops at the limit but never drops a pinned tag", () => {
  const many = Array.from({ length: 20 }, (_, index) => tag(`t${String(index).padStart(2, "0")}`));
  const busy = counts(Object.fromEntries(many.map((item, index) => [item.id, index + 1])));
  assert.equal(sidebarTags(many, busy, null).length, sidebarTagLimit);
  const pinned = many.slice(0, 10).map((item) => ({ ...item, pinned: true }));
  assert.equal(sidebarTags([...pinned, ...many.slice(10)], busy, null).length, 10, "all ten pinned tags show and nothing else");
});

test("archived tags leave the sidebar unless they are the open tag", () => {
  const tags = [tag("old", { archived: true }), tag("bug")];
  const shown = (active: string | null) => sidebarTags(tags, counts({ old: 5, bug: 1 }), active).map((item) => item.path);
  assert.deepEqual(shown(null), ["bug"]);
  assert.deepEqual(shown("old"), ["bug", "old"]);
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
