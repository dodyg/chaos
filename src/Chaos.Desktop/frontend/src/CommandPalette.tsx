import React from "react";
import { CornerDownLeft, Search } from "lucide-react";
import { cn } from "@/lib/utils";
import { listShortcuts } from "./listKeyboard";

export type PaletteCommand = {
  id: string;
  group: string;
  label: string;
  icon?: React.ReactNode;
  hint?: string;
  keywords?: string;
  run: () => void;
};

export type PaletteTask = { id: string; title: string; detail: string };

/** Every word typed must appear somewhere in the command's text, in any order. */
export function matchesQuery(command: Pick<PaletteCommand, "label" | "group" | "keywords">, query: string) {
  const haystack = `${command.label} ${command.group} ${command.keywords ?? ""}`.toLowerCase();
  return query.toLowerCase().split(/\s+/).filter(Boolean).every(word => haystack.includes(word));
}

/**
 * Ctrl+K: one box to jump to a view, list or tag, run an action, or open a task found by
 * search. Arrow keys move, Enter runs, Escape closes. Task results come from the same search
 * as the sidebar box once two characters are typed.
 */
export function CommandPalette({ commands, searchTasks, onOpenTask, onClose }: {
  commands: PaletteCommand[];
  searchTasks: (text: string) => Promise<PaletteTask[]>;
  onOpenTask: (taskId: string) => void;
  onClose: () => void;
}) {
  const [query, setQuery] = React.useState("");
  const [active, setActive] = React.useState(0);
  const [tasks, setTasks] = React.useState<PaletteTask[]>([]);
  const input = React.useRef<HTMLInputElement>(null);
  const list = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { input.current?.focus(); }, []);

  React.useEffect(() => {
    const text = query.trim();
    if (text.length < 2) { setTasks([]); return; }
    let current = true;
    const timer = window.setTimeout(() => void searchTasks(text).then(found => { if (current) setTasks(found); }).catch(() => { if (current) setTasks([]); }), 150);
    return () => { current = false; window.clearTimeout(timer); };
  }, [query, searchTasks]);

  const shown = commands.filter(command => matchesQuery(command, query)).slice(0, 40);
  const items: PaletteCommand[] = [...shown, ...tasks.map(task => ({ id: `task:${task.id}`, group: "Tasks", label: task.title, hint: task.detail, run: () => onOpenTask(task.id) }))];
  React.useEffect(() => { setActive(0); }, [query, tasks.length]);
  React.useEffect(() => { list.current?.querySelector<HTMLElement>(`[data-index="${active}"]`)?.scrollIntoView?.({ block: "nearest" }); }, [active]);

  function run(item: PaletteCommand | undefined) {
    if (!item) return;
    onClose();
    item.run();
  }
  function keyDown(event: React.KeyboardEvent) {
    if (event.key === "ArrowDown") { event.preventDefault(); setActive(index => Math.min(items.length - 1, index + 1)); }
    else if (event.key === "ArrowUp") { event.preventDefault(); setActive(index => Math.max(0, index - 1)); }
    else if (event.key === "Enter") { event.preventDefault(); run(items[active]); }
    else if (event.key === "Escape") { event.preventDefault(); onClose(); }
  }

  let lastGroup = "";
  return <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 px-4 pt-[12vh]" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <div role="dialog" aria-modal="true" aria-label="Command palette" className="grid w-full max-w-xl overflow-hidden rounded-xl border bg-popover text-popover-foreground shadow-2xl" onKeyDown={keyDown}>
      <div className="flex items-center gap-2 border-b px-3">
        <Search className="size-4 shrink-0 text-muted-foreground"/>
        <input ref={input} role="combobox" aria-expanded="true" aria-controls="palette-results" aria-activedescendant={items[active] ? `palette-${active}` : undefined}
          aria-label="Search commands, lists and tasks" placeholder="Jump to a list, run a command or find a task…" autoComplete="off" spellCheck={false}
          className="h-12 min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground" value={query} onChange={event => setQuery(event.target.value)}/>
        <kbd className="rounded border px-1.5 py-0.5 text-[0.65rem] text-muted-foreground">Esc</kbd>
      </div>
      <div ref={list} id="palette-results" role="listbox" aria-label="Results" className="max-h-[50vh] overflow-y-auto p-1.5">
        {items.length === 0 && <p className="px-3 py-6 text-center text-sm text-muted-foreground">{query.trim().length < 2 ? "Type to search." : "Nothing matches."}</p>}
        {items.map((item, index) => {
          const heading = item.group !== lastGroup;
          lastGroup = item.group;
          return <React.Fragment key={item.id}>
            {heading && <div role="presentation" className="px-2 pt-2 pb-1 text-[0.65rem] font-semibold tracking-wider text-muted-foreground uppercase">{item.group}</div>}
            <div id={`palette-${index}`} data-index={index} role="option" aria-selected={index === active}
              className={cn("flex cursor-default items-center gap-2.5 rounded-md px-2 py-1.5 text-sm [&_svg]:size-4 [&_svg]:shrink-0 [&_svg]:text-muted-foreground", index === active && "bg-accent text-accent-foreground")}
              onMouseMove={() => setActive(index)} onClick={() => run(item)}>
              {item.icon}<span className="min-w-0 flex-1 truncate">{item.label}</span>
              {item.hint && <span className="shrink-0 truncate text-xs text-muted-foreground">{item.hint}</span>}
              {index === active && <CornerDownLeft className="size-3.5"/>}
            </div>
          </React.Fragment>;
        })}
      </div>
      <div className="flex flex-wrap gap-x-3 gap-y-1 border-t px-3 py-2 text-[0.65rem] text-muted-foreground" aria-label="Keyboard shortcuts in task lists">
        {listShortcuts.map(([keys, action]) => <span key={keys}><kbd className="font-sans font-semibold">{keys}</kbd> {action}</span>)}
      </div>
    </div>
  </div>;
}
