import type { TagResponse } from "#neoastra";

/** How many tags the sidebar shows before the rest move behind "All tags". */
export const sidebarTagLimit = 8;

const byPath = (a: TagResponse, b: TagResponse) => a.path.localeCompare(b.path, undefined, { sensitivity: "base" });

/**
 * The tags worth a sidebar row: pinned tags first (always all of them), then the busiest tags
 * that have open tasks, up to the limit. Archived tags never appear, except that the tag the
 * person is looking at stays visible so the sidebar still shows where they are.
 */
export function sidebarTags(tags: readonly TagResponse[], counts: ReadonlyMap<string, number>, activeTagId: string | null): TagResponse[] {
  const live = tags.filter((tag) => !tag.archived);
  const pinned = live.filter((tag) => tag.pinned).sort(byPath);
  const busiest = live
    .filter((tag) => !tag.pinned && (counts.get(tag.id) ?? 0) > 0)
    .sort((a, b) => (counts.get(b.id) ?? 0) - (counts.get(a.id) ?? 0) || byPath(a, b))
    .slice(0, Math.max(0, sidebarTagLimit - pinned.length));
  const shown = [...pinned, ...busiest];
  const active = tags.find((tag) => tag.id === activeTagId);
  return active && !shown.includes(active) ? [...shown, active] : shown;
}

export type TagSort = "name" | "usage";

/** Tags for the Tags page: filtered by a text match on the path, then sorted. */
export function listTags(
  tags: readonly TagResponse[],
  counts: ReadonlyMap<string, number>,
  options: { filter: string; sort: TagSort; showArchived: boolean },
): TagResponse[] {
  const needle = options.filter.trim().toLowerCase();
  return tags
    .filter((tag) => (options.showArchived ? tag.archived : !tag.archived))
    .filter((tag) => !needle || tag.path.toLowerCase().includes(needle))
    .sort((a, b) => (options.sort === "usage" ? (counts.get(b.id) ?? 0) - (counts.get(a.id) ?? 0) || byPath(a, b) : byPath(a, b)));
}
