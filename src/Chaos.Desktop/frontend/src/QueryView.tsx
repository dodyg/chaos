import React from "react";
import { queryRpc, recurrenceRpc, type UpcomingRecurrenceDto, type QueryRowResponse, type TagResponse, type TaskQueryRequest, type TaskResponse } from "#neoastra";
import { CalendarDays, FolderKanban, SearchX, UserRound } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { EmptyState, Field, InlineError, PriorityBadge, StatusBadge, isFinished, label, priorities, statuses } from "@/components/common";
import { createLatestRequest } from "./latestRequest";
import { GitHubTaskMark } from "./GitHubTaskMark";
import { TagChip, taskDragType } from "./tags";

/** A built-in view (PLAN §10) or a search; tag views carry the tag, search carries the words. */
export type QueryState = { view: string; tagId?: string; text?: string; day?: string };

export const builtInViews = [
  { view: "Today", title: "Today", hint: "Open tasks planned or due today." },
  { view: "Upcoming", title: "Upcoming", hint: "Planned or due in the next 14 days, by the earliest date." },
  { view: "Overdue", title: "Overdue", hint: "Due dates and recurring schedules that have passed. An ordinary planned date alone is not overdue." },
  { view: "Waiting", title: "Waiting", hint: "Tasks waiting on someone outside the app." },
  { view: "RecentlyCompleted", title: "Recently completed", hint: "Done in the last 30 days, newest first." },
  { view: "AssignedToMe", title: "Assigned to me", hint: "Every open task in Personal is yours; shared spaces arrive later." },
] as const;

type Filters = { status: string; priority: string; includeCompleted: boolean; includeArchived: boolean; dueFrom: string; dueTo: string };
const noFilters: Filters = { status: "", priority: "", includeCompleted: false, includeArchived: false, dueFrom: "", dueTo: "" };

function request(query: QueryState, filters: Filters, cursor: string | null): TaskQueryRequest {
  return {
    view: query.view, containerId: null, tagId: query.tagId ?? null, text: query.text?.trim() || null,
    statuses: filters.status ? [filters.status] : [], priorities: filters.priority ? [filters.priority] : [],
    plannedFrom: null, plannedTo: null, dueFrom: filters.dueFrom || null, dueTo: filters.dueTo || null, completedFrom: null, completedTo: null,
    includeCompleted: filters.includeCompleted, includeArchived: filters.includeArchived, upcomingDays: null, limit: null, cursor, day: query.day ?? null,
  };
}

/**
 * Rows of one built-in view, tag group or search with temporary filters that compose with it
 * (nothing is saved). Pages are fetched with the server's cursor; the total comes from the
 * same query, so it matches the sidebar count for the unfiltered view.
 */
export function QueryView({ query, tags, revision, selectedTaskId, busy, api = queryRpc, recurrenceApi = recurrenceRpc, onOpen, header }: {
  query: QueryState; tags: TagResponse[]; revision: number; selectedTaskId: string | null; busy: boolean;
  api?: typeof queryRpc; recurrenceApi?: typeof recurrenceRpc; onOpen: (task: TaskResponse) => void; header: React.ReactNode;
}) {
  const [filters, setFilters] = React.useState<Filters>(noFilters);
  const [rows, setRows] = React.useState<QueryRowResponse[]>([]);
  const [total, setTotal] = React.useState(0);
  const [cursor, setCursor] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [projected, setProjected] = React.useState<readonly UpcomingRecurrenceDto[]>([]);
  const requests = React.useRef(createLatestRequest());
  const key = `${query.view}|${query.tagId ?? ""}|${query.text ?? ""}`;

  React.useEffect(() => { setFilters(noFilters); }, [key]);
  React.useEffect(() => {
    let current = true;
    setProjected([]);
    if (query.view === "Upcoming" && JSON.stringify(filters) === JSON.stringify(noFilters)) {
      void recurrenceApi.upcoming({ days: 14, limit: 100 }).then(result => {
        if (!current) return;
        if (result.ok) setProjected(result.items); else setError(result.error?.message ?? "Could not load projected repetitions.");
      }).catch((error: unknown) => { if (current) setError(String(error)); });
    }
    return () => { current = false; };
  }, [key, filters, revision, recurrenceApi]);
  React.useEffect(() => {
    const isCurrent = requests.current.begin();
    setLoading(true); setError(null);
    void api.run(request(query, filters, null)).then(result => {
      if (!isCurrent()) return;
      if (!result.ok) { setError(result.error?.message ?? "Could not load this view."); setRows([]); setTotal(0); setCursor(null); return; }
      setRows([...result.rows]); setTotal(result.totalCount); setCursor(result.nextCursor);
    }).catch((e: unknown) => { if (isCurrent()) setError(String(e)); }).finally(() => { if (isCurrent()) setLoading(false); });
  }, [api, key, filters, revision]);

  async function loadMore() {
    if (!cursor) return;
    const isCurrent = requests.current.begin();
    setLoading(true);
    try {
      const result = await api.run(request(query, filters, cursor));
      if (!isCurrent()) return;
      if (!result.ok) throw new Error(result.error?.message ?? "Could not load more tasks.");
      setRows(current => [...current, ...result.rows]); setTotal(result.totalCount); setCursor(result.nextCursor);
    } catch (e) { if (isCurrent()) setError(e instanceof Error ? e.message : "Could not load more tasks."); }
    finally { if (isCurrent()) setLoading(false); }
  }

  const byId = new Map(tags.map(tag => [tag.id, tag]));
  const change = (patch: Partial<Filters>) => setFilters(current => ({ ...current, ...patch }));
  const grouped = query.view === "Upcoming" || query.view === "Overdue";
  const showCompletedToggle = query.view !== "RecentlyCompleted" && query.view !== "Today" && query.view !== "Upcoming" && query.view !== "Overdue" && query.view !== "Waiting";

  return <>
    {header}
    <form className="flex flex-wrap items-end gap-3 rounded-xl border bg-muted/40 p-3" aria-label="Filters" onSubmit={event => event.preventDefault()}>
      <Field label="Status" className="w-40"><NativeSelect value={filters.status} onChange={e => change({ status: e.target.value })}>
        <NativeSelectOption value="">Any status</NativeSelectOption>
        {statuses.map(status => <NativeSelectOption key={status} value={status}>{label(status)}</NativeSelectOption>)}
      </NativeSelect></Field>
      <Field label="Priority" className="w-36"><NativeSelect value={filters.priority} onChange={e => change({ priority: e.target.value })}>
        <NativeSelectOption value="">Any priority</NativeSelectOption>
        {priorities.map(priority => <NativeSelectOption key={priority} value={priority}>{priority}</NativeSelectOption>)}
      </NativeSelect></Field>
      <Field label="Due from" className="w-40"><Input type="date" value={filters.dueFrom} onChange={e => change({ dueFrom: e.target.value })}/></Field>
      <Field label="Due to" className="w-40"><Input type="date" value={filters.dueTo} onChange={e => change({ dueTo: e.target.value })}/></Field>
      {showCompletedToggle && <label className="flex h-9 items-center gap-2 text-sm"><input type="checkbox" checked={filters.includeCompleted} onChange={e => change({ includeCompleted: e.target.checked })}/>Include completed</label>}
      <label className="flex h-9 items-center gap-2 text-sm"><input type="checkbox" checked={filters.includeArchived} onChange={e => change({ includeArchived: e.target.checked })}/>Include archived lists</label>
      {JSON.stringify(filters) !== JSON.stringify(noFilters) && <Button type="button" variant="ghost" onClick={() => setFilters(noFilters)}>Clear filters</Button>}
    </form>

    {error && <InlineError>{error}</InlineError>}
    <p className="text-sm text-muted-foreground" role="status" aria-live="polite">{loading && rows.length === 0 ? "Loading…" : `${total} ${total === 1 ? "task" : "tasks"}`}</p>
    {!loading && !error && rows.length === 0 && projected.length === 0 && <EmptyState title={query.view === "Search" ? "No matching tasks." : "Nothing here."}>
      {query.view === "Search" ? <span className="inline-flex items-center gap-1"><SearchX className="size-4"/>Try fewer words, or include completed tasks and archived lists.</span> : "Tasks appear here as soon as they match."}
    </EmptyState>}
    {projected.length > 0 && <section className="grid gap-2" aria-label="Projected repetitions"><h3 className="font-semibold">Projected repetitions</h3><p className="text-xs text-muted-foreground">These are read-only estimates. A task becomes available after its current occurrence is finished.</p><ol className="grid gap-1">{projected.map(item => <li key={`${item.sourceTaskId}|${item.nominalLocal}`} className="rounded-lg border bg-muted/40 px-3 py-2 text-sm">{item.title} · {item.nominalLocal.replace("T", " ")} <Badge variant="outline">Projected</Badge></li>)}</ol></section>}
    {rows.length > 0 && <ol className="grid gap-1" aria-label="Results">{rows.map((row, index) => {
      const heading = grouped && row.groupDate && row.groupDate !== rows[index - 1]?.groupDate;
      const personHeading = query.view === "Waiting" && (index === 0 || row.waitingPerson !== rows[index - 1]?.waitingPerson);
      const task = row.task;
      return <React.Fragment key={task.id}>
        {heading && <li aria-hidden="true" className="flex items-center gap-1.5 border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase"><CalendarDays className="size-3.5"/>{row.groupDate}</li>}
        {personHeading && <li aria-hidden="true" className="flex items-center gap-1.5 border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase"><UserRound className="size-3.5"/><bdi>{row.waitingPerson ?? "No person named"}</bdi></li>}
        <li draggable={!busy} onDragStart={event => { event.dataTransfer.effectAllowed = "copy"; event.dataTransfer.setData(taskDragType, task.id); event.dataTransfer.setData("text/plain", task.title); }}
          className={cn("flex min-h-11 items-center gap-2 rounded-lg border bg-card px-2 py-1.5 shadow-xs hover:border-input", selectedTaskId === task.id && "border-primary ring-1 ring-primary", isFinished(task.status) && "[&_[data-slot=row-title]]:text-muted-foreground [&_[data-slot=row-title]]:line-through")}>
          <div className="grid min-w-0 flex-1 gap-1">
            <div className="flex items-center gap-1">
              <button type="button" data-slot="row-title" className="w-fit min-w-0 rounded-sm px-1 py-0.5 text-left font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={busy} onClick={() => onOpen(task)}>{task.title}</button>
              <GitHubTaskMark taskId={task.id} version={task.version} revision={revision}/>
            </div>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-xs text-muted-foreground">
              <span className="inline-flex items-center gap-1"><FolderKanban className="size-3.5"/>{row.containerName}{row.containerArchived && " · archived"}</span>
              {task.dueDate && <span>Due {task.dueDate}{task.dueTime ? ` ${task.dueTime.slice(0, 5)}` : ""}</span>}
              {task.plannedDate && <span>Planned {task.plannedDate}</span>}
              {query.view === "Overdue" && !task.dueDate && <Badge variant="outline">Missed recurrence</Badge>}
              {row.waitingPerson && query.view !== "Waiting" && <span className="inline-flex items-center gap-1"><UserRound className="size-3.5"/>Waiting on <bdi>{row.waitingPerson}</bdi></span>}
              {row.nextFollowUpAtUtc && <Badge variant="outline">Follow up {new Date(row.nextFollowUpAtUtc).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}</Badge>}
              <StatusBadge status={task.status}/>
              <PriorityBadge priority={task.priority}/>
              {row.matches.filter(match => match !== "Task").map(match => <Badge key={match} variant="outline">{match === "WaitingPerson" ? "Matched the waiting person" : `Matched in ${match === "Subtask" ? "a subtask" : "the diary"}`}</Badge>)}
              {row.tagIds.map(id => byId.get(id)).filter((tag): tag is TagResponse => tag !== undefined).map(tag => <TagChip key={tag.id} tag={tag}/>)}
            </div>
          </div>
        </li>
      </React.Fragment>;
    })}</ol>}
    {cursor && <Button type="button" variant="outline" className="justify-self-center" disabled={loading} onClick={() => void loadMore()}>Load more ({total - rows.length} more)</Button>}
  </>;
}
