export type NavSection = "main" | "views" | "notes";
export const navSections: readonly NavSection[] = ["main", "views", "notes"];

/** A saved placement: the flat list is the order within each section. */
export type NavPlacement = { id: string; section: NavSection };

const storageKey = "chaos.sidebar.layout";

export function loadNavLayout(): NavPlacement[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "[]");
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((item): item is NavPlacement => typeof item?.id === "string" && navSections.includes(item.section as NavSection));
  } catch {
    return [];
  }
}

export function saveNavLayout(layout: readonly NavPlacement[]) {
  try {
    localStorage.setItem(storageKey, JSON.stringify(layout));
  } catch {
    /* storage unavailable */
  }
}

/**
 * Item ids per section: saved placements first (ignoring items that no longer exist), then
 * any item with no saved placement in its default section, in default order.
 */
export function resolveNavLayout(defaults: readonly NavPlacement[], saved: readonly NavPlacement[]): Record<NavSection, string[]> {
  const known = new Set(defaults.map((item) => item.id));
  const result: Record<NavSection, string[]> = { main: [], views: [], notes: [] };
  const placed = new Set<string>();
  for (const item of saved) {
    if (!known.has(item.id) || placed.has(item.id)) continue;
    placed.add(item.id);
    result[item.section].push(item.id);
  }
  for (const item of defaults) if (!placed.has(item.id)) result[item.section].push(item.id);
  return result;
}

/** Moves `id` into `section` at `index` (index after removal), returning the new flat layout. */
export function moveNavItem(resolved: Record<NavSection, string[]>, id: string, section: NavSection, index: number): NavPlacement[] {
  const next: Record<NavSection, string[]> = {
    main: resolved.main.filter((item) => item !== id),
    views: resolved.views.filter((item) => item !== id),
    notes: resolved.notes.filter((item) => item !== id),
  };
  next[section].splice(Math.max(0, Math.min(index, next[section].length)), 0, id);
  return navSections.flatMap((name) => next[name].map((item) => ({ id: item, section: name })));
}
