import type { TagResponse } from "#neoastra";

/** How many tags the sidebar shows before the rest move behind "All tags". */
export const sidebarTagLimit = 8;

const byPath = (a: TagResponse, b: TagResponse) => a.path.localeCompare(b.path, undefined, { sensitivity: "base" });

export type TagSort = "name" | "usage";

/** A tag with its nested tags. */
export type TagNode = { tag: TagResponse; children: TagNode[] };

function compare(counts: ReadonlyMap<string, number>, sort: TagSort) {
  return (a: TagResponse, b: TagResponse) =>
    sort === "usage" ? (counts.get(b.id) ?? 0) - (counts.get(a.id) ?? 0) || byPath(a, b) : byPath(a, b);
}

/** Tags nested under their parents; a tag whose parent is not in the list becomes a root. */
export function tagForest(tags: readonly TagResponse[], counts: ReadonlyMap<string, number>, sort: TagSort): TagNode[] {
  const order = compare(counts, sort);
  const ids = new Set(tags.map((tag) => tag.id));
  const children = new Map<string | null, TagResponse[]>();
  for (const tag of tags) {
    const key = tag.parentId && ids.has(tag.parentId) ? tag.parentId : null;
    children.set(key, [...(children.get(key) ?? []), tag]);
  }
  const build = (parent: string | null): TagNode[] =>
    (children.get(parent) ?? []).sort(order).map((tag) => ({ tag, children: build(tag.id) }));
  return build(null);
}

/** The tree flattened to the rows showing now: children of a collapsed tag are skipped. */
export function visibleRows(
  nodes: readonly TagNode[],
  collapsed: ReadonlySet<string>,
  depth = 0,
): { tag: TagResponse; depth: number; hasChildren: boolean }[] {
  return nodes.flatMap((node) => [
    { tag: node.tag, depth, hasChildren: node.children.length > 0 },
    ...(collapsed.has(node.tag.id) ? [] : visibleRows(node.children, collapsed, depth + 1)),
  ]);
}

/** A sidebar row: a tag and its live nested tags. `label` is the full path for a nested tag shown on its own. */
export type SidebarEntry = { tag: TagResponse; label: string; children: TagNode[] };

/** Archived tags, and the tags nested under them, are out of the sidebar. */
function liveTags(tags: readonly TagResponse[]): TagResponse[] {
  const archived = tags.filter((tag) => tag.archived).map((tag) => `${tag.path}/`);
  return tags.filter((tag) => !tag.archived && !archived.some((prefix) => tag.path.startsWith(prefix)));
}

/**
 * The sidebar's shortlist: pinned tags first (at any depth, nested ones under their full path),
 * then the busiest top-level tags that have open tasks, up to the limit. Each entry carries its
 * nested tags, which the sidebar shows when it is expanded. A top-level count already includes
 * its nested tags. The open tag always stays visible so the sidebar shows where you are.
 */
export function sidebarEntries(
  tags: readonly TagResponse[],
  counts: ReadonlyMap<string, number>,
  activeTagId: string | null,
): SidebarEntry[] {
  const live = liveTags(tags);
  const nodes = new Map<string, TagNode>();
  const index = (list: TagNode[]) => list.forEach((node) => (nodes.set(node.tag.id, node), index(node.children)));
  const forest = tagForest(live, counts, "usage");
  index(forest);
  const entry = (tag: TagResponse): SidebarEntry => ({
    tag,
    label: tag.parentId ? tag.path : tag.name,
    children: nodes.get(tag.id)?.children ?? [],
  });

  const pinned = live.filter((tag) => tag.pinned).sort(byPath);
  const busiest = forest
    .map((node) => node.tag)
    .filter((tag) => !tag.pinned && (counts.get(tag.id) ?? 0) > 0)
    .slice(0, Math.max(0, sidebarTagLimit - pinned.length));
  const entries = [...pinned, ...busiest].map(entry);

  const active = tags.find((tag) => tag.id === activeTagId);
  const covered = (tag: TagResponse) =>
    entries.some((item) => item.tag.id === tag.id || (live.includes(tag) && tag.path.startsWith(`${item.tag.path}/`)));
  return active && !covered(active) ? [...entries, { tag: active, label: active.path, children: [] }] : entries;
}

/** Tags for the Tags page when it is searching or showing archived ones: a flat list, filtered and sorted. */
export function listTags(
  tags: readonly TagResponse[],
  counts: ReadonlyMap<string, number>,
  options: { filter: string; sort: TagSort; showArchived: boolean },
): TagResponse[] {
  const needle = options.filter.trim().toLowerCase();
  return tags
    .filter((tag) => (options.showArchived ? tag.archived : !tag.archived))
    .filter((tag) => !needle || tag.path.toLowerCase().includes(needle))
    .sort(compare(counts, options.sort));
}
