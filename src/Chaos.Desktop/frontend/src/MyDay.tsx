import React from "react";
import { ContextMenu } from "radix-ui";
import { myDayRpc, queryRpc, type waitingRpc, type DayClockResponse, type DaySuggestionResponse, type DaySummaryResponse, type QueryRowResponse, type TaskQueryRequest, type TaskResponse } from "#neoastra";
import { AlertTriangle, CalendarArrowUp, ImagePlus, ChevronLeft, ChevronRight, Clock, Eye, FolderKanban, Lightbulb, Plus, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Count, EmptyState, InlineError, PageHeader, PriorityBadge, StatusBadge, isFinished, label, statuses } from "@/components/common";
import { createLatestRequest } from "./latestRequest";
import { useDragReorder } from "./dragReorder";
import { taskDragType } from "./tags";
import { FollowUps } from "./Reminders";
import { DayBackgroundSettings, type DayBackground } from "./MyDayBackground";
import { CategoryEdge, CategoryMenuItems, asCategory, categories, categoryIcon, categoryTone, laneTotals, type Category, type Focus } from "./category";

const operationId = () => crypto.randomUUID();

/** Adds whole days to a yyyy-MM-dd local date (calendar arithmetic, no time zone involved). */
export function addDays(date: string, days: number) {
  const [year, month, day] = date.split("-").map(Number);
  const value = new Date(Date.UTC(year, month - 1, day + days));
  return value.toISOString().slice(0, 10);
}

/** Delay before checking the local date again: just after the next local midnight, and at least once a minute (resume, sleep, zone changes). */
export function nextCheckDelay(clock: DayClockResponse, now = Date.now()) {
  const untilMidnight = Date.parse(clock.nextDayStartsAtUtc) - now + 1000;
  return Math.max(1000, Math.min(untilMidnight, 60_000));
}

/**
 * The profile's local today, kept current without a restart (PLAN §10): it is read again
 * just after local midnight, every minute, and whenever the window regains focus or becomes
 * visible after sleep. The returned clock only changes identity when the date or zone does.
 */
export function useDayClock(api: Pick<typeof myDayRpc, "clock"> = myDayRpc) {
  const [clock, setClock] = React.useState<DayClockResponse | null>(null);
  React.useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function check() {
      clearTimeout(timer);
      try {
        const result = await api.clock({});
        if (!alive) return;
        if (result.ok && result.clock) {
          const next = result.clock;
          setClock(current => current && current.today === next.today && current.zoneId === next.zoneId ? current : next);
          timer = setTimeout(() => void check(), nextCheckDelay(next));
          return;
        }
      } catch { /* retried below */ }
      if (alive) timer = setTimeout(() => void check(), 60_000);
    }
    function wake() { if (document.visibilityState === "visible") void check(); }
    void check();
    window.addEventListener("focus", wake);
    document.addEventListener("visibilitychange", wake);
    return () => { alive = false; clearTimeout(timer); window.removeEventListener("focus", wake); document.removeEventListener("visibilitychange", wake); };
  }, [api]);
  return clock;
}

const reasonText: Record<string, string> = {
  Overdue: "Overdue",
  DueToday: "Due today",
  PlannedToday: "Planned today",
  DueSoon: "Due soon",
  RecurrenceToday: "Repeats today",
  UnfinishedYesterday: "Unfinished yesterday",
};

function dayRequest(day: string, finished: boolean, focus: Focus): TaskQueryRequest {
  return {
    view: "MyDay", containerId: null, tagId: null, text: null, statuses: finished ? ["Done", "Cancelled"] : [], priorities: [],
    plannedFrom: null, plannedTo: null, dueFrom: null, dueTo: null, completedFrom: null, completedTo: null,
    includeCompleted: false, includeArchived: true, upcomingDays: null, limit: 100, cursor: null, day, category: focus,
  };
}

function minutes(total: number) {
  const hours = Math.floor(total / 60);
  return hours > 0 ? (total % 60 ? `${hours} h ${total % 60} min` : `${hours} h`) : `${total} min`;
}

const categoryOf = (task: TaskResponse): Category => asCategory(task.category) ?? "Personal";

/** "1 h 30 min of 6 h" against a limit from My Week; over the limit is flagged, never acted on. */
function Against({ used, limit, label }: { used: number; limit: number | null; label: string }) {
  if (limit === null) return null;
  const over = used > limit;
  return <span className={cn("inline-flex items-center gap-1 tabular-nums", over && "font-medium text-destructive")} title={`${label} from My Week`}>
    of {minutes(limit)}{over && <><AlertTriangle className="size-3.5"/><span>{label === "Work time" ? "More work than planned" : "More than this day holds"}</span></>}
  </span>;
}

/**
 * Maps an insertion index inside one lane to the day's single manual order: the moved task
 * lands just before the lane neighbour it was dropped above, or just after the lane's last task.
 */
export function laneToDayIndex(day: readonly string[], lane: readonly string[], taskId: string, laneIndex: number) {
  const dayWithout = day.filter(id => id !== taskId);
  const laneWithout = lane.filter(id => id !== taskId);
  if (laneWithout.length === 0) return day.indexOf(taskId);
  return laneIndex < laneWithout.length ? dayWithout.indexOf(laneWithout[laneIndex]) : dayWithout.indexOf(laneWithout[laneWithout.length - 1]) + 1;
}

/**
 * My Day (PLAN §10): the tasks chosen for one local date in manual order, a collapsible
 * finished section, explicit suggestions that are never added by themselves, and carry-over
 * to the next day. Earlier days are read-only and stay reviewable. Choosing a task here never
 * changes its planned or due date.
 */
export function MyDay({ clock, revision, selectedTaskId, busy, api = myDayRpc, queryApi = queryRpc, waitingApi, onOpen, onView, onChanged, onSetStatus, background, onBackgroundChange, focus = null, onSetCategory }: {
  clock: DayClockResponse | null; revision: number; selectedTaskId: string | null; busy: boolean;
  api?: typeof myDayRpc; queryApi?: typeof queryRpc; waitingApi?: typeof waitingRpc; onOpen: (task: TaskResponse) => void; onView?: (task: TaskResponse) => void; onChanged: () => void; onSetStatus?: (task: TaskResponse, status: string) => void; background?: DayBackground; onBackgroundChange?: (background: DayBackground) => boolean;
  /** Work/Personal focus; null shows both, split into Work and Personal lanes. */
  focus?: Focus; onSetCategory?: (task: TaskResponse, category: Category) => void;
}) {
  const today = clock?.today ?? null;
  const [day, setDay] = React.useState<string | null>(today);
  const [summary, setSummary] = React.useState<DaySummaryResponse | null>(null);
  const [open, setOpen] = React.useState<QueryRowResponse[]>([]);
  const [finished, setFinished] = React.useState<QueryRowResponse[]>([]);
  const [suggestions, setSuggestions] = React.useState<DaySuggestionResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);
  const [local, setLocal] = React.useState(0);
  const [laneDrop, setLaneDrop] = React.useState<Category | null>(null);
  const [showBackground, setShowBackground] = React.useState(false);
  const requests = React.useRef(createLatestRequest());
  const shownToday = React.useRef(today);
  const drag = useDragReorder();

  // When the local date rolls over, a person looking at "today" moves to the new today;
  // someone reviewing another date stays there. Yesterday's plan is never deleted.
  React.useEffect(() => {
    if (today === null) return;
    setDay(current => current === null || current === shownToday.current ? today : current);
    shownToday.current = today;
  }, [today]);

  React.useEffect(() => {
    if (day === null || today === null) return;
    const isCurrent = requests.current.begin();
    setError(null);
    void (async () => {
      const opened = await api.open({ date: day });
      if (!isCurrent()) return;
      if (!opened.ok || !opened.day) { setError(opened.error?.message ?? "Could not open this day."); return; }
      const [rows, done, suggested] = await Promise.all([
        queryApi.run(dayRequest(day, false, focus)),
        queryApi.run(dayRequest(day, true, focus)),
        day === today ? api.suggestions({}) : Promise.resolve(null),
      ]);
      if (!isCurrent()) return;
      setSummary(opened.day);
      if (!rows.ok) setError(rows.error?.message ?? "Could not load this day.");
      setOpen(rows.ok ? [...rows.rows] : []);
      setFinished(done.ok ? [...done.rows] : []);
      setSuggestions(suggested?.ok ? [...suggested.suggestions] : []);
    })().catch((e: unknown) => { if (isCurrent()) setError(String(e)); });
  }, [api, queryApi, day, today, revision, local, focus]);

  async function change(action: () => ReturnType<typeof api.add>, failure: string, notify = true) {
    if (working || busy) return;
    setWorking(true); setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? failure);
      if (result.day) setSummary(result.day);
      setLocal(value => value + 1);
      if (notify) onChanged();
    } catch (e) { setError(e instanceof Error ? e.message : failure); }
    finally { setWorking(false); }
  }

  if (day === null || today === null) return <p className="text-sm text-muted-foreground" role="status">Loading My Day…</p>;
  const editable = day >= today;
  const tomorrow = addDays(day, 1);
  const canCarry = tomorrow >= today && open.length > 0;
  const disabled = busy || working;
  const title = day === today ? "My Day" : day === addDays(today, -1) ? "Yesterday" : day === addDays(today, 1) ? "Tomorrow" : day;
  const add = (taskId: string) => change(() => api.add({ operationId: operationId(), date: day, taskId }), "Could not add the task to My Day.");
  const order = open.map(row => row.task.id);
  const move = (taskId: string, index: number) => {
    if (!editable || disabled || index < 0 || index >= order.length || order[index] === taskId) return;
    void change(() => api.move({ operationId: operationId(), date: day, taskId, index }), "Could not reorder My Day.", false);
  };
  const shownSuggestions = focus ? suggestions.filter(item => categoryOf(item.task) === focus) : suggestions;
  const totals = laneTotals(open.map(row => row.task));
  // Lanes appear when the day mixes Work and Personal; a single-category day stays one list.
  const mixed = new Set(open.map(row => categoryOf(row.task))).size > 1;
  const lanes = focus || !mixed ? null : categories.map(category => ({ category, rows: open.filter(row => categoryOf(row.task) === category) }));
  const canRecategorize = !!onSetCategory && editable && !disabled;
  /** A lane accepts a task row dragged from the other lane and switches its category. */
  function laneDropTarget(category: Category) {
    const accepts = (event: React.DragEvent<HTMLElement>) => canRecategorize && Array.from(event.dataTransfer.types ?? []).includes(taskDragType);
    return {
      onDragOver: (event: React.DragEvent<HTMLElement>) => { if (!accepts(event)) return; event.preventDefault(); event.dataTransfer.dropEffect = "copy"; setLaneDrop(category); },
      onDragLeave: (event: React.DragEvent<HTMLElement>) => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setLaneDrop(null); },
      onDrop: (event: React.DragEvent<HTMLElement>) => {
        setLaneDrop(null);
        if (event.defaultPrevented || !accepts(event)) return;
        const row = open.find(item => item.task.id === event.dataTransfer.getData(taskDragType));
        if (!row || categoryOf(row.task) === category) return;
        event.preventDefault();
        onSetCategory?.(row.task, category);
      },
    };
  }
  const rowList = (rows: QueryRowResponse[], name: string, group: string) => {
    const laneOrder = rows.map(row => row.task.id);
    const toDay = (taskId: string, laneIndex: number) => lanes ? laneToDayIndex(order, laneOrder, taskId, laneIndex) : laneIndex;
    return <ol className="grid gap-1" aria-label={name}>{rows.map((row, index) => <DayRow key={row.task.id} row={row} selected={selectedTaskId === row.task.id} busy={disabled} onOpen={onOpen} onView={onView} onSetStatus={editable ? onSetStatus : undefined}
      onSetCategory={onSetCategory}
      reorderTarget={drag.target(laneOrder, index, group, disabled || !editable, (id, laneIndex) => move(id, toDay(id, laneIndex)))}
      dragHandle={editable && drag.handle(row.task.id, group, disabled, undefined, { label: `Reorder ${row.task.title}`, onMove: offset => { const target = index + offset; if (target >= 0 && target < rows.length) move(row.task.id, toDay(row.task.id, target)); } })}>
      {editable && <div className="flex gap-0.5">
        <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={disabled} aria-label={`Remove ${row.task.title} from this day`} title="Remove from this day"
          onClick={() => void change(() => api.remove({ operationId: operationId(), date: day, taskId: row.task.id }), "Could not remove the task.")}><X/></Button>
      </div>}
    </DayRow>)}</ol>;
  };

  return <>
    <PageHeader eyebrow={day === today ? "Today" : "My Day"} title={title}
      subtitle={day === today ? `Your chosen tasks for ${day}. Adding a task here never changes its planned or due date.`
        : editable ? `Planned ahead for ${day}.` : `A look back at ${day}. Earlier days are kept as they were.`}
      actions={<nav aria-label="Day" className="flex items-center gap-1">
        {background && onBackgroundChange && <Button type="button" variant={showBackground ? "secondary" : "ghost"} size="icon-sm" className="mr-1 text-muted-foreground" aria-label="My Day background" title="Change the My Day background"
          aria-expanded={showBackground} aria-controls="day-background-settings" onClick={() => setShowBackground(value => !value)}><ImagePlus/></Button>}
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Previous day" disabled={working} onClick={() => setDay(addDays(day, -1))}><ChevronLeft/></Button>
        <Button type="button" variant={day === today ? "secondary" : "outline"} size="sm" disabled={working || day === today} onClick={() => setDay(today)}>Today</Button>
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Next day" disabled={working} onClick={() => setDay(addDays(day, 1))}><ChevronRight/></Button>
      </nav>}/>

    {background && onBackgroundChange && showBackground && <DayBackgroundSettings background={background} onSave={onBackgroundChange} onClose={() => setShowBackground(false)}/>}

    {error && <InlineError>{error}</InlineError>}
    {summary && !lanes && <p className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground" role="status" aria-live="polite">
      {focus && <span className={cn("inline-flex items-center gap-1 font-medium [&_svg]:size-3.5", categoryTone[focus].text)}>{categoryIcon[focus]}{focus} only</span>}
      <span>{totals.open} open · {finished.length} finished</span>
      <span className="inline-flex items-center gap-1"><Clock className="size-3.5"/>{minutes(totals.minutes)} estimated
        {focus === "Work" ? <Against used={totals.minutes} limit={summary.workAllowanceMinutes} label="Work time"/> : focus === null && <Against used={totals.minutes} limit={summary.availableMinutes} label="Available time"/>}
        {totals.unknown > 0 && ` · ${totals.unknown} without an estimate`}</span>
    </p>}
    {summary && lanes && <div className="grid grid-cols-2 gap-2" role="status" aria-live="polite" aria-label="Today by category">
      {lanes.map(lane => { const lt = laneTotals(lane.rows.map(row => row.task)); return <div key={lane.category} className={cn("grid gap-0.5 rounded-lg border border-l-4 bg-card/80 px-3 py-2 shadow-xs", categoryTone[lane.category].border)}>
        <span className={cn("inline-flex items-center gap-1.5 text-xs font-semibold tracking-wider uppercase [&_svg]:size-3.5", categoryTone[lane.category].text)}>{categoryIcon[lane.category]}{lane.category}</span>
        <span className="flex flex-wrap items-center gap-x-1 text-sm font-medium tabular-nums">{lt.open} open · {minutes(lt.minutes)}{lane.category === "Work" && <Against used={lt.minutes} limit={summary.workAllowanceMinutes} label="Work time"/>}</span>
        {lt.unknown > 0 && <span className="text-xs text-muted-foreground">{lt.unknown} without an estimate</span>}
      </div>; })}
      <p className="col-span-2 flex flex-wrap items-center gap-x-1 text-xs text-muted-foreground">{finished.length} finished · {minutes(totals.minutes)} planned<Against used={totals.minutes} limit={summary.availableMinutes} label="Available time"/></p>
    </div>}

    {open.length === 0
      ? <EmptyState title={editable ? "Nothing chosen yet." : "Nothing was left open on this day."}>
          {day === today ? "Pick from the suggestions below or add a task from its editor." : editable ? "Carry tasks over or add them from a task's editor." : "Finished tasks are listed below."}
        </EmptyState>
      : lanes
        ? <div className="grid gap-4">{lanes.map(lane => <section key={lane.category} aria-label={`${lane.category} tasks`} {...laneDropTarget(lane.category)}
            className={cn("grid gap-1.5 rounded-lg border-l-4 py-1 pl-3 transition-colors", categoryTone[lane.category].border, laneDrop === lane.category && cn(categoryTone[lane.category].soft, "ring-1 ring-primary"))}>
            <h3 className={cn("flex items-center gap-1.5 text-xs font-semibold tracking-wider uppercase [&_svg]:size-3.5", categoryTone[lane.category].text)}>{categoryIcon[lane.category]}{lane.category}<Count>{lane.rows.length}</Count></h3>
            {lane.rows.length === 0
              ? <p className="rounded-lg border border-dashed px-3 py-2 text-sm text-muted-foreground">No {lane.category.toLowerCase()} tasks chosen.{canRecategorize && ` Drag a task here to make it ${lane.category}.`}</p>
              : rowList(lane.rows, `${lane.category} chosen tasks`, `${day}|${lane.category}`)}
          </section>)}</div>
        : rowList(open, "Chosen tasks", day)}

    {editable && waitingApi && <FollowUps day={day} revision={revision + local} busy={disabled} api={waitingApi} onOpen={onOpen} onChanged={onChanged}/>}

    {canCarry && <div><Button type="button" variant="outline" size="sm" disabled={disabled}
      onClick={() => void change(() => api.carryOver({ operationId: operationId(), fromDate: day, taskIds: null }), "Could not carry the tasks over.")}>
      <CalendarArrowUp/>Carry unfinished to {tomorrow === today ? "today" : tomorrow === addDays(today, 1) ? "tomorrow" : tomorrow}</Button></div>}

    {finished.length > 0 && <details className="group/done grid gap-1">
      <summary className="flex cursor-pointer list-none items-center border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase select-none [&::-webkit-details-marker]:hidden">
        <ChevronRight className="mr-1 size-3.5 transition-transform group-open/done:rotate-90"/>Finished<Count>{finished.length}</Count>
      </summary>
      <ol className="grid gap-1 pt-1" aria-label="Finished tasks">{finished.map(row => <DayRow key={row.task.id} row={row} selected={selectedTaskId === row.task.id} busy={disabled} onOpen={onOpen} onView={onView} onSetStatus={editable ? onSetStatus : undefined} onSetCategory={onSetCategory}/>)}</ol>
    </details>}

    {day === today && shownSuggestions.length === 0 && <p className="inline-flex items-center gap-1.5 px-1 text-xs text-muted-foreground" role="status">
      <Lightbulb className="size-3.5"/>{focus && suggestions.length > 0 ? `No ${focus.toLowerCase()} suggestions right now.` : "No suggestions right now."}</p>}
    {day === today && shownSuggestions.length > 0 && <section aria-labelledby="suggestions-heading" className="grid gap-1.5 rounded-xl border bg-muted/40 p-3">
      <h3 id="suggestions-heading" className="flex items-center gap-1.5 font-semibold"><Lightbulb className="size-4 text-primary"/>Suggestions<Count>{shownSuggestions.length}</Count></h3>
      <p className="text-xs text-muted-foreground">Nothing is added by itself. Choose what fits today.</p>
      <ol className="grid gap-1" aria-label="Suggested tasks">{shownSuggestions.map(item => <DayRow key={item.task.id} row={{ task: item.task, containerName: item.containerName }} selected={selectedTaskId === item.task.id} busy={disabled} onOpen={onOpen} onView={onView}
            badge={<Badge variant="outline">{reasonText[item.reason] ?? item.reason}</Badge>}>
            <Button type="button" variant="outline" size="sm" disabled={disabled} aria-label={`Add ${item.task.title} to My Day`} onClick={() => void add(item.task.id)}><Plus/>Add</Button>
          </DayRow>)}</ol>
    </section>}
  </>;
}

function DayRow({ row, selected, busy, onOpen, onView, onSetStatus, onSetCategory, badge, children, dragHandle, reorderTarget }: {
  row: { task: TaskResponse; containerName: string }; selected: boolean; busy: boolean; onOpen: (task: TaskResponse) => void; onView?: (task: TaskResponse) => void; onSetStatus?: (task: TaskResponse, status: string) => void; onSetCategory?: (task: TaskResponse, category: Category) => void; badge?: React.ReactNode; children?: React.ReactNode;
  dragHandle?: React.ReactNode; reorderTarget?: ReturnType<ReturnType<typeof useDragReorder>["target"]>;
}) {
  const task = row.task;
  const done = task.status === "Done" || task.status === "Cancelled";
  return <li><ContextMenu.Root><ContextMenu.Trigger asChild><div {...reorderTarget} draggable={!busy} onDragStart={event => { event.dataTransfer.effectAllowed = "copy"; event.dataTransfer.setData(taskDragType, task.id); event.dataTransfer.setData("text/plain", task.title); }}
    className={cn("flex min-h-11 items-center gap-2 rounded-lg border bg-card py-1.5 pr-2 pl-1.5 shadow-xs hover:border-input", selected && "border-primary ring-1 ring-primary", done && "[&_[data-slot=row-title]]:text-muted-foreground [&_[data-slot=row-title]]:line-through")}>
    <CategoryEdge category={task.category}/>
    {onSetStatus && <input type="checkbox" className="size-4 shrink-0 accent-primary" aria-label={`Mark ${task.title} as done`} checked={task.status === "Done"} disabled={busy} onChange={e => onSetStatus(task, e.target.checked ? "Done" : "NotStarted")}/>}
    {dragHandle}
    <div className="grid min-w-0 flex-1 gap-1">
      <button type="button" data-slot="row-title" className="w-fit rounded-sm px-1 py-0.5 text-left font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={busy} onClick={() => onOpen(task)}>{task.title}</button>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-xs text-muted-foreground">
        {badge}
        <span className="inline-flex items-center gap-1"><FolderKanban className="size-3.5"/>{row.containerName}</span>
        {task.dueDate && <span>Due {task.dueDate}{task.dueTime ? ` ${task.dueTime.slice(0, 5)}` : ""}</span>}
        {task.plannedDate && <span>Planned {task.plannedDate}</span>}
        {task.estimateMinutes !== null && <span className="inline-flex items-center gap-1"><Clock className="size-3.5"/>{task.estimateMinutes} min</span>}
        <StatusBadge status={task.status}/>
        <PriorityBadge priority={task.priority}/>
      </div>
    </div>
    {onView && <Button type="button" variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy} aria-label={`View ${task.title}`} title="View" onClick={() => onView(task)}><Eye/></Button>}
    {children}
  </div></ContextMenu.Trigger>{(onSetStatus || onSetCategory) && <ContextMenu.Portal><ContextMenu.Content aria-label={`Status for ${task.title}`} className="z-50 min-w-44 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-lg">
    {onSetStatus && <><ContextMenu.Label className="px-2 py-1.5 text-xs text-muted-foreground">Set status</ContextMenu.Label>
    {(isFinished(task.status) ? ["NotStarted"] : statuses.filter(status => status !== "Done")).map(status => <ContextMenu.Item key={status} disabled={busy || status === task.status} className="cursor-default rounded-sm px-2 py-1.5 outline-none data-[highlighted]:bg-accent data-[disabled]:opacity-50" onSelect={() => onSetStatus(task, status)}>{isFinished(task.status) ? "Reopen (previous status)" : label(status)}</ContextMenu.Item>)}</>}
    {onSetCategory && <CategoryMenuItems category={task.category} busy={busy} separator={!!onSetStatus} onSelect={category => onSetCategory(task, category)}/>}
  </ContextMenu.Content></ContextMenu.Portal>}</ContextMenu.Root></li>;
}
