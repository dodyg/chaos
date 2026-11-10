import React from "react";
import { myDayRpc, queryRpc, type waitingRpc, type DayClockResponse, type DaySuggestionResponse, type DaySummaryResponse, type QueryRowResponse, type TaskQueryRequest, type TaskResponse } from "#neoastra";
import { ArrowDown, ArrowUp, CalendarArrowUp, ChevronLeft, ChevronRight, Clock, FolderKanban, Lightbulb, Plus, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Count, EmptyState, InlineError, PageHeader, PriorityBadge, StatusBadge } from "@/components/common";
import { createLatestRequest } from "./latestRequest";
import { taskDragType } from "./tags";
import { FollowUps } from "./Reminders";

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

function dayRequest(day: string, finished: boolean): TaskQueryRequest {
  return {
    view: "MyDay", containerId: null, tagId: null, text: null, statuses: finished ? ["Done", "Cancelled"] : [], priorities: [],
    plannedFrom: null, plannedTo: null, dueFrom: null, dueTo: null, completedFrom: null, completedTo: null,
    includeCompleted: false, includeArchived: true, upcomingDays: null, limit: 100, cursor: null, day,
  };
}

function minutes(total: number) {
  const hours = Math.floor(total / 60);
  return hours > 0 ? `${hours} h ${total % 60} min` : `${total} min`;
}

/**
 * My Day (PLAN §10): the tasks chosen for one local date in manual order, a collapsible
 * finished section, explicit suggestions that are never added by themselves, and carry-over
 * to the next day. Earlier days are read-only and stay reviewable. Choosing a task here never
 * changes its planned or due date.
 */
export function MyDay({ clock, revision, selectedTaskId, busy, api = myDayRpc, queryApi = queryRpc, waitingApi, onOpen, onChanged }: {
  clock: DayClockResponse | null; revision: number; selectedTaskId: string | null; busy: boolean;
  api?: typeof myDayRpc; queryApi?: typeof queryRpc; waitingApi?: typeof waitingRpc; onOpen: (task: TaskResponse) => void; onChanged: () => void;
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
  const requests = React.useRef(createLatestRequest());
  const shownToday = React.useRef(today);

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
        queryApi.run(dayRequest(day, false)),
        queryApi.run(dayRequest(day, true)),
        day === today ? api.suggestions({}) : Promise.resolve(null),
      ]);
      if (!isCurrent()) return;
      setSummary(opened.day);
      if (!rows.ok) setError(rows.error?.message ?? "Could not load this day.");
      setOpen(rows.ok ? [...rows.rows] : []);
      setFinished(done.ok ? [...done.rows] : []);
      setSuggestions(suggested?.ok ? [...suggested.suggestions] : []);
    })().catch((e: unknown) => { if (isCurrent()) setError(String(e)); });
  }, [api, queryApi, day, today, revision, local]);

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

  return <>
    <PageHeader eyebrow={day === today ? "Today" : "My Day"} title={title}
      subtitle={day === today ? `Your chosen tasks for ${day}. Adding a task here never changes its planned or due date.`
        : editable ? `Planned ahead for ${day}.` : `A look back at ${day}. Earlier days are kept as they were.`}
      actions={<nav aria-label="Day" className="flex items-center gap-1">
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Previous day" disabled={working} onClick={() => setDay(addDays(day, -1))}><ChevronLeft/></Button>
        <Button type="button" variant={day === today ? "secondary" : "outline"} size="sm" disabled={working || day === today} onClick={() => setDay(today)}>Today</Button>
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Next day" disabled={working} onClick={() => setDay(addDays(day, 1))}><ChevronRight/></Button>
      </nav>}/>

    {error && <InlineError>{error}</InlineError>}
    {summary && <p className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground" role="status" aria-live="polite">
      <span>{summary.openCount} open · {summary.finishedCount} finished</span>
      <span className="inline-flex items-center gap-1"><Clock className="size-3.5"/>{minutes(summary.remainingMinutes)} estimated{summary.unknownEstimates > 0 && ` · ${summary.unknownEstimates} without an estimate`}</span>
    </p>}

    {open.length === 0
      ? <EmptyState title={editable ? "Nothing chosen yet." : "Nothing was left open on this day."}>
          {day === today ? "Pick from the suggestions below or add a task from its editor." : editable ? "Carry tasks over or add them from a task's editor." : "Finished tasks are listed below."}
        </EmptyState>
      : <ol className="grid gap-1" aria-label="Chosen tasks">{open.map((row, index) => <DayRow key={row.task.id} row={row} selected={selectedTaskId === row.task.id} busy={disabled} onOpen={onOpen}>
          {editable && <div className="flex gap-0.5">
            <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={disabled || index === 0} aria-label={`Move ${row.task.title} up`} title="Move up"
              onClick={() => void change(() => api.move({ operationId: operationId(), date: day, taskId: row.task.id, index: index - 1 }), "Could not reorder My Day.", false)}><ArrowUp/></Button>
            <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={disabled || index === open.length - 1} aria-label={`Move ${row.task.title} down`} title="Move down"
              onClick={() => void change(() => api.move({ operationId: operationId(), date: day, taskId: row.task.id, index: index + 1 }), "Could not reorder My Day.", false)}><ArrowDown/></Button>
            <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={disabled} aria-label={`Remove ${row.task.title} from this day`} title="Remove from this day"
              onClick={() => void change(() => api.remove({ operationId: operationId(), date: day, taskId: row.task.id }), "Could not remove the task.")}><X/></Button>
          </div>}
        </DayRow>)}</ol>}

    {editable && waitingApi && <FollowUps day={day} revision={revision + local} busy={disabled} api={waitingApi} onOpen={onOpen} onChanged={onChanged}/>}

    {canCarry && <div><Button type="button" variant="outline" size="sm" disabled={disabled}
      onClick={() => void change(() => api.carryOver({ operationId: operationId(), fromDate: day, taskIds: null }), "Could not carry the tasks over.")}>
      <CalendarArrowUp/>Carry unfinished to {tomorrow === today ? "today" : tomorrow === addDays(today, 1) ? "tomorrow" : tomorrow}</Button></div>}

    {finished.length > 0 && <details className="group/done grid gap-1">
      <summary className="flex cursor-pointer list-none items-center border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase select-none [&::-webkit-details-marker]:hidden">
        <ChevronRight className="mr-1 size-3.5 transition-transform group-open/done:rotate-90"/>Finished<Count>{finished.length}</Count>
      </summary>
      <ol className="grid gap-1 pt-1" aria-label="Finished tasks">{finished.map(row => <DayRow key={row.task.id} row={row} selected={selectedTaskId === row.task.id} busy={disabled} onOpen={onOpen}/>)}</ol>
    </details>}

    {day === today && <section aria-labelledby="suggestions-heading" className="grid gap-1.5 rounded-xl border bg-muted/40 p-3">
      <h3 id="suggestions-heading" className="flex items-center gap-1.5 font-semibold"><Lightbulb className="size-4 text-primary"/>Suggestions<Count>{suggestions.length}</Count></h3>
      <p className="text-xs text-muted-foreground">Nothing is added by itself. Choose what fits today.</p>
      {suggestions.length === 0
        ? <p className="py-2 text-sm text-muted-foreground">No suggestions right now.</p>
        : <ol className="grid gap-1" aria-label="Suggested tasks">{suggestions.map(item => <DayRow key={item.task.id} row={{ task: item.task, containerName: item.containerName }} selected={selectedTaskId === item.task.id} busy={disabled} onOpen={onOpen}
            badge={<Badge variant="outline">{reasonText[item.reason] ?? item.reason}</Badge>}>
            <Button type="button" variant="outline" size="sm" disabled={disabled} aria-label={`Add ${item.task.title} to My Day`} onClick={() => void add(item.task.id)}><Plus/>Add</Button>
          </DayRow>)}</ol>}
    </section>}
  </>;
}

function DayRow({ row, selected, busy, onOpen, badge, children }: {
  row: { task: TaskResponse; containerName: string }; selected: boolean; busy: boolean; onOpen: (task: TaskResponse) => void; badge?: React.ReactNode; children?: React.ReactNode;
}) {
  const task = row.task;
  const done = task.status === "Done" || task.status === "Cancelled";
  return <li draggable={!busy} onDragStart={event => { event.dataTransfer.effectAllowed = "copy"; event.dataTransfer.setData(taskDragType, task.id); event.dataTransfer.setData("text/plain", task.title); }}
    className={cn("flex min-h-11 items-center gap-2 rounded-lg border bg-card px-2 py-1.5 shadow-xs hover:border-input", selected && "border-primary ring-1 ring-primary", done && "[&_[data-slot=row-title]]:text-muted-foreground [&_[data-slot=row-title]]:line-through")}>
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
    {children}
  </li>;
}
