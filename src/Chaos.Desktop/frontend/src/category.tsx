import React from "react";
import { ContextMenu } from "radix-ui";
import { Briefcase, House, Layers } from "lucide-react";
import { cn } from "@/lib/utils";

/** A task's capacity category (PLAN §5 Tasks). */
export type Category = "Work" | "Personal";
export const categories: readonly Category[] = ["Work", "Personal"];

/** The Work/Personal focus: null shows both. */
export type Focus = Category | null;

const focusKey = "chaos.focus";

/** The remembered focus for this viewer; storage failures fall back to showing both. */
export function useFocus() {
  const [focus, setFocusState] = React.useState<Focus>(() => {
    try { const stored = localStorage.getItem(focusKey); return stored === "Work" || stored === "Personal" ? stored : null; } catch { return null; }
  });
  const setFocus = React.useCallback((next: Focus) => {
    setFocusState(next);
    try { if (next) localStorage.setItem(focusKey, next); else localStorage.removeItem(focusKey); } catch { /* storage unavailable */ }
  }, []);
  return [focus, setFocus] as const;
}

export function asCategory(value: string | null | undefined): Category | null {
  return value === "Work" || value === "Personal" ? value : null;
}

export const categoryIcon: Record<Category, React.ReactNode> = { Work: <Briefcase/>, Personal: <House/> };

/** Colour tokens per category: a solid for marks and dots, a soft wash for lane chrome. */
export const categoryTone: Record<Category, { solid: string; text: string; soft: string; border: string }> = {
  Work: { solid: "bg-sky-500", text: "text-sky-600 dark:text-sky-400", soft: "bg-sky-500/10", border: "border-sky-500/60" },
  Personal: { solid: "bg-emerald-500", text: "text-emerald-600 dark:text-emerald-400", soft: "bg-emerald-500/10", border: "border-emerald-500/60" },
};

/** The sidebar switch: All, Work or Personal. It filters every task view and sets the category of new tasks. */
export function FocusSwitch({ focus, onChange }: { focus: Focus; onChange: (focus: Focus) => void }) {
  const options: { value: Focus; label: string; icon: React.ReactNode }[] = [
    { value: null, label: "All", icon: <Layers/> },
    { value: "Work", label: "Work", icon: categoryIcon.Work },
    { value: "Personal", label: "Personal", icon: categoryIcon.Personal },
  ];
  return <div role="radiogroup" aria-label="Focus" title="Focus: show Work, Personal or all tasks in every view"
    className="mb-2 grid grid-cols-3 gap-0.5 rounded-md border border-sidebar-border bg-background/40 p-0.5 text-xs">
    {options.map(option => {
      const checked = focus === option.value;
      return <button key={option.label} type="button" role="radio" aria-checked={checked}
        className={cn("inline-flex h-7 items-center justify-center gap-1 rounded-[5px] px-1 font-medium text-muted-foreground outline-none hover:text-sidebar-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 [&_svg]:size-3.5",
          checked && "bg-sidebar-accent text-sidebar-foreground shadow-xs", checked && option.value && categoryTone[option.value].text)}
        onClick={() => onChange(option.value)}>{option.icon}{option.label}</button>;
    })}
  </div>;
}

/** A thin coloured edge at a task row's start that names its category for assistive technology too. */
export function CategoryEdge({ category }: { category: string }) {
  const value = asCategory(category) ?? "Personal";
  return <span role="img" aria-label={value} title={value} className={cn("w-1 self-stretch shrink-0 rounded-full", categoryTone[value].solid)}/>;
}

/** A small dot for a list's default category. */
export function CategoryDot({ category, className }: { category: Category; className?: string }) {
  return <span role="img" aria-label={`Default ${category}`} title={`New tasks here are ${category}`} className={cn("size-2 shrink-0 rounded-full", categoryTone[category].solid, className)}/>;
}

/** Context-menu items that switch a task between Work and Personal. */
export function CategoryMenuItems({ category, busy, onSelect, separator = true }: { category: string; busy: boolean; onSelect: (category: Category) => void; separator?: boolean }) {
  return <>
    {separator && <ContextMenu.Separator className="my-1 h-px bg-border"/>}
    <ContextMenu.Label className="px-2 py-1.5 text-xs text-muted-foreground">Category</ContextMenu.Label>
    {categories.map(value => <ContextMenu.Item key={value} disabled={busy || value === category}
      className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 outline-none data-[highlighted]:bg-accent data-[disabled]:opacity-50 [&_svg]:size-3.5"
      onSelect={() => onSelect(value)}><span className={categoryTone[value].text}>{categoryIcon[value]}</span>{value}</ContextMenu.Item>)}
  </>;
}

/** Remaining minutes, open count and unknown estimates for a set of open tasks. */
export function laneTotals(tasks: readonly { estimateMinutes: number | null }[]) {
  return {
    open: tasks.length,
    minutes: tasks.reduce((sum, task) => sum + (task.estimateMinutes ?? 0), 0),
    unknown: tasks.filter(task => task.estimateMinutes === null).length,
  };
}
