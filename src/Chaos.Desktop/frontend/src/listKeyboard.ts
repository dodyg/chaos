import type React from "react";

/** Keys the task lists answer to, shown in the command palette's help. */
export const listShortcuts: readonly [string, string][] = [
  ["J / ↓", "Next task"],
  ["K / ↑", "Previous task"],
  ["Enter", "Open the task"],
  ["V", "View the task"],
  ["X", "Mark done or reopen"],
  ["M", "Add to My Day"],
];

const rowSelector = "[data-task-row]";
const titleSelector = "[data-slot=row-title]";

/** Row titles in reading order, skipping rows inside a folded section. */
function rowTitles(root: HTMLElement) {
  return Array.from(root.querySelectorAll<HTMLElement>(`${rowSelector} ${titleSelector}`)).filter(title => !title.closest("details:not([open])"));
}

/**
 * Keyboard use of every task list in the main area (My Day, lists, views): move between rows
 * with J/K or the arrow keys, Enter opens (the title is a button), V views, X toggles done
 * and M adds the task to My Day. Text fields and open menus keep their own keys.
 */
export function handleListKeys(event: React.KeyboardEvent<HTMLElement>, onAddToMyDay: (taskId: string) => void) {
  if (event.ctrlKey || event.metaKey || event.altKey || event.defaultPrevented) return;
  const target = event.target as HTMLElement;
  if (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName) || target.closest("[role=menu]")) return;
  const key = event.key.toLowerCase();
  const titles = rowTitles(event.currentTarget);
  if (titles.length === 0) return;
  const row = target.closest<HTMLElement>(rowSelector);
  const current = row ? titles.findIndex(title => row.contains(title)) : -1;

  if (key === "j" || key === "arrowdown" || key === "k" || key === "arrowup") {
    // Arrow keys inside a row's own controls (a drag handle moves the task) stay with them.
    if ((key === "arrowdown" || key === "arrowup") && row && target !== titles[current]) return;
    event.preventDefault();
    const step = key === "j" || key === "arrowdown" ? 1 : -1;
    const next = current < 0 ? (step > 0 ? 0 : titles.length - 1) : Math.min(titles.length - 1, Math.max(0, current + step));
    titles[next].focus();
    titles[next].scrollIntoView?.({ block: "nearest" });
    return;
  }
  if (!row || current < 0) return;
  if (key === "x") {
    const box = row.querySelector<HTMLInputElement>("input[type=checkbox]");
    if (box && !box.disabled) { event.preventDefault(); box.click(); }
  } else if (key === "v") {
    const view = row.querySelector<HTMLButtonElement>("button[aria-label^='View ']");
    if (view && !view.disabled) { event.preventDefault(); view.click(); }
  } else if (key === "m") {
    const id = row.dataset.taskRow;
    if (id) { event.preventDefault(); onAddToMyDay(id); }
  }
}
