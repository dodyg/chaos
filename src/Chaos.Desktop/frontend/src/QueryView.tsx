import { TaskViewAction } from "./TaskViewAction";
import React from "react";
import { TaskStaleness } from "./TaskStaleness";
import { DateText, friendlyDate, useToday } from "./dates";
import {
  queryRpc,
  recurrenceRpc,
  viewRpc,
  boardRpc,
  calendarRpc,
  tagRpc,
  type ContainerResponse,
  type UpcomingRecurrenceDto,
  type QueryRowResponse,
  type TagResponse,
  type TaskQueryRequest,
  type TaskResponse,
} from "#neoastra";
import { CalendarDays, FolderKanban, Handshake, SearchX, UserRound } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { EmptyState, Field, InlineError, PriorityBadge, StatusBadge, isFinished, label, priorities, statuses } from "@/components/common";
import { createLatestRequest } from "./latestRequest";
import { GitHubTaskMark } from "./GitHubTaskMark";
import { TagChip, taskDragType } from "./tags";
import { CategoryEdge, CategoryMenuItems, type Category, type Focus } from "./category";
import { ContextMenu } from "radix-ui";
import { ViewTable, type TableTaskApi } from "./ViewTable";
import { Board } from "./Board";
import { Calendar } from "./Calendar";
import { defaultLayout, layoutFromJson, type Layout } from "./viewModel";
import { AssigneeChips, useAssigneeChips } from "./assignees";

/** A built-in view (PLAN §10) or a search; tag views carry the tag, search carries the words. */
export type QueryState = { view: string; tagId?: string; text?: string; day?: string; viewId?: string };

export const builtInViews = [
  { view: "Today", title: "Today", hint: "Open tasks planned or due today." },
  { view: "Upcoming", title: "Upcoming", hint: "Planned or due in the next 14 days, by the earliest date." },
  {
    view: "Overdue",
    title: "Overdue",
    hint: "Due dates and recurring schedules that have passed. An ordinary planned date alone is not overdue.",
  },
  { view: "Waiting", title: "Waiting", hint: "Tasks waiting on someone outside the app." },
  { view: "RecentlyCompleted", title: "Recently completed", hint: "Done in the last 30 days, newest first." },
  { view: "AssignedToMe", title: "Assigned to me", hint: "Open tasks in Personal and shared tasks assigned to you." },
  { view: "DelegatedByMe", title: "Delegated by me", hint: "Tasks you delegated to members and that still await an answer or your review, by person then due date." },
] as const;

type Filters = { status: string; priority: string; includeCompleted: boolean; includeArchived: boolean; dueFrom: string; dueTo: string };
const noFilters: Filters = { status: "", priority: "", includeCompleted: false, includeArchived: false, dueFrom: "", dueTo: "" };

function request(query: QueryState, filters: Filters, cursor: string | null, focus: Focus): TaskQueryRequest {
  return {
    view: query.view,
    containerId: null,
    tagId: query.tagId ?? null,
    text: query.text?.trim() || null,
    statuses: filters.status ? [filters.status] : [],
    priorities: filters.priority ? [filters.priority] : [],
    plannedFrom: null,
    plannedTo: null,
    dueFrom: filters.dueFrom || null,
    dueTo: filters.dueTo || null,
    completedFrom: null,
    completedTo: null,
    includeCompleted: filters.includeCompleted,
    includeArchived: filters.includeArchived,
    upcomingDays: null,
    limit: null,
    cursor,
    day: query.day ?? null,
    category: focus,
  };
}

/**
 * Rows of one built-in view, tag group or search with temporary filters that compose with it
 * (nothing is saved). Pages are fetched with the server's cursor; the total comes from the
 * same query, so it matches the sidebar count for the unfiltered view.
 */
export function QueryView({
  query,
  tags,
  revision,
  selectedTaskId,
  busy,
  api = queryRpc,
  recurrenceApi = recurrenceRpc,
  viewApi = viewRpc,
  tableApi,
  onTaskUpdated,
  boardApi,
  onChanged,
  tagApi,
  onOpen,
  header,
  focus = null,
  onSetCategory,
  containers,
  calendarApi,
}: {
  query: QueryState;
  tags: TagResponse[];
  revision: number;
  selectedTaskId: string | null;
  busy: boolean;
  api?: typeof queryRpc;
  recurrenceApi?: typeof recurrenceRpc;
  /** Custom views (PLAN2 P3): a saved view's rows come from here, not from the built-in query. */
  viewApi?: Pick<typeof viewRpc, "run">;
  tableApi?: TableTaskApi;
  onTaskUpdated?: (task: TaskResponse) => void;
  /** The board layout of a view reads and moves cards through the board service. */
  boardApi?: typeof boardRpc;
  onChanged?: () => void;
  tagApi?: typeof tagRpc;
  onOpen: (task: TaskResponse) => void;
  header: React.ReactNode;
  /** Work/Personal focus; null shows both. */
  focus?: Focus;
  onSetCategory?: (task: TaskResponse, category: Category) => void;
  /** The calendar layout of a view filters by list and reads through the calendar service. */
  containers?: ContainerResponse[];
  calendarApi?: typeof calendarRpc;
}) {
  const [filters, setFilters] = React.useState<Filters>(noFilters);
  const [rows, setRows] = React.useState<QueryRowResponse[]>([]);
  const assignmentInfo = useAssigneeChips(
    rows.map((row) => row.task),
    revision,
  );
  const [total, setTotal] = React.useState(0);
  const [cursor, setCursor] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [projected, setProjected] = React.useState<readonly UpcomingRecurrenceDto[]>([]);
  const requests = React.useRef(createLatestRequest());
  const [layout, setLayout] = React.useState<Layout>(defaultLayout);
  const isCustom = query.view === "Custom";
  const key = `${query.view}|${query.tagId ?? ""}|${query.text ?? ""}|${query.viewId ?? ""}|${focus ?? ""}`;

  /** One page of the open view: a saved custom view through views.run, anything else through queries.run. */
  const fetchPage = React.useCallback(
    async (cursor: string | null) => {
      if (isCustom) {
        const result = await viewApi.run({ viewId: query.viewId ?? "", category: focus, limit: null, cursor });
        return {
          ok: result.ok,
          rows: result.rows,
          totalCount: result.totalCount,
          nextCursor: result.nextCursor,
          error: result.error,
          layoutJson: result.layoutJson,
        };
      }
      const result = await api.run(request(query, filters, cursor, focus));
      return {
        ok: result.ok,
        rows: result.rows,
        totalCount: result.totalCount,
        nextCursor: result.nextCursor,
        error: result.error,
        layoutJson: null,
      };
    },
    [api, viewApi, key, filters],
  );

  React.useEffect(() => {
    setFilters(noFilters);
  }, [key]);
  React.useEffect(() => {
    let current = true;
    setProjected([]);
    if (query.view === "Upcoming" && focus === null && JSON.stringify(filters) === JSON.stringify(noFilters)) {
      void recurrenceApi
        .upcoming({ days: 14, limit: 100 })
        .then((result) => {
          if (!current) return;
          if (result.ok) setProjected(result.items);
          else setError(result.error?.message ?? "Could not load projected repetitions.");
        })
        .catch((error: unknown) => {
          if (current) setError(String(error));
        });
    }
    return () => {
      current = false;
    };
  }, [key, filters, revision, recurrenceApi]);
  React.useEffect(() => {
    const isCurrent = requests.current.begin();
    setLoading(true);
    setError(null);
    void fetchPage(null)
      .then((result) => {
        if (!isCurrent()) return;
        if (!result.ok) {
          setError(result.error?.message ?? "Could not load this view.");
          setRows([]);
          setTotal(0);
          setCursor(null);
          return;
        }
        if (result.layoutJson) setLayout(layoutFromJson(result.layoutJson));
        setRows([...result.rows]);
        setTotal(result.totalCount);
        setCursor(result.nextCursor);
      })
      .catch((e: unknown) => {
        if (isCurrent()) setError(String(e));
      })
      .finally(() => {
        if (isCurrent()) setLoading(false);
      });
  }, [fetchPage, revision]);

  async function loadMore() {
    if (!cursor) return;
    const isCurrent = requests.current.begin();
    setLoading(true);
    try {
      const result = await fetchPage(cursor);
      if (!isCurrent()) return;
      if (!result.ok) throw new Error(result.error?.message ?? "Could not load more tasks.");
      setRows((current) => [...current, ...result.rows]);
      setTotal(result.totalCount);
      setCursor(result.nextCursor);
    } catch (e) {
      if (isCurrent()) setError(e instanceof Error ? e.message : "Could not load more tasks.");
    } finally {
      if (isCurrent()) setLoading(false);
    }
  }

  const today = useToday();
  // A list layout's grouping orders the rows already loaded; the saved sort still decides which come first.
  const shown = React.useMemo(
    () =>
      isCustom && layout.kind === "list" && layout.group !== "none"
        ? [...rows].sort((a, b) => listGroup(a, layout.group, today).order - listGroup(b, layout.group, today).order)
        : rows,
    [rows, isCustom, layout.kind, layout.group, today],
  );
  const byId = new Map(tags.map((tag) => [tag.id, tag]));
  const change = (patch: Partial<Filters>) => setFilters((current) => ({ ...current, ...patch }));
  const grouped = query.view === "Upcoming" || query.view === "Overdue";
  const showCompletedToggle =
    query.view !== "RecentlyCompleted" &&
    query.view !== "Today" &&
    query.view !== "Upcoming" &&
    query.view !== "Overdue" &&
    query.view !== "Waiting";

  return (
    <>
      {header}
      {!isCustom && (
        <form
          className="flex flex-wrap items-end gap-3 rounded-xl border bg-muted/40 p-3"
          aria-label="Filters"
          onSubmit={(event) => event.preventDefault()}
        >
          <Field label="Status" className="w-40">
            <NativeSelect value={filters.status} onChange={(e) => change({ status: e.target.value })}>
              <NativeSelectOption value="">Any status</NativeSelectOption>
              {statuses.map((status) => (
                <NativeSelectOption key={status} value={status}>
                  {label(status)}
                </NativeSelectOption>
              ))}
            </NativeSelect>
          </Field>
          <Field label="Priority" className="w-36">
            <NativeSelect value={filters.priority} onChange={(e) => change({ priority: e.target.value })}>
              <NativeSelectOption value="">Any priority</NativeSelectOption>
              {priorities.map((priority) => (
                <NativeSelectOption key={priority} value={priority}>
                  {priority}
                </NativeSelectOption>
              ))}
            </NativeSelect>
          </Field>
          <Field label="Due from" className="w-40">
            <Input type="date" value={filters.dueFrom} onChange={(e) => change({ dueFrom: e.target.value })} />
          </Field>
          <Field label="Due to" className="w-40">
            <Input type="date" value={filters.dueTo} onChange={(e) => change({ dueTo: e.target.value })} />
          </Field>
          {showCompletedToggle && (
            <label className="flex h-9 items-center gap-2 text-sm">
              <input type="checkbox" checked={filters.includeCompleted} onChange={(e) => change({ includeCompleted: e.target.checked })} />
              Include completed
            </label>
          )}
          <label className="flex h-9 items-center gap-2 text-sm">
            <input type="checkbox" checked={filters.includeArchived} onChange={(e) => change({ includeArchived: e.target.checked })} />
            Include archived lists
          </label>
          {JSON.stringify(filters) !== JSON.stringify(noFilters) && (
            <Button type="button" variant="ghost" onClick={() => setFilters(noFilters)}>
              Clear filters
            </Button>
          )}
        </form>
      )}

      {error && <InlineError>{error}</InlineError>}
      <p className="text-sm text-muted-foreground" role="status" aria-live="polite">
        {loading && rows.length === 0 ? "Loading…" : `${total} ${total === 1 ? "task" : "tasks"}`}
      </p>
      {!loading && !error && rows.length === 0 && projected.length === 0 && (
        <EmptyState title={query.view === "Search" ? "No matching tasks." : "Nothing here."}>
          {query.view === "Search" ? (
            <span className="inline-flex items-center gap-1">
              <SearchX className="size-4" />
              Try fewer words, or include completed tasks and archived lists.
            </span>
          ) : (
            "Tasks appear here as soon as they match."
          )}
        </EmptyState>
      )}
      {projected.length > 0 && (
        <section className="grid gap-2" aria-label="Projected repetitions">
          <h3 className="font-semibold">Projected repetitions</h3>
          <p className="text-xs text-muted-foreground">
            These are read-only estimates. A task becomes available after its current occurrence is finished.
          </p>
          <ol className="grid gap-1">
            {projected.map((item) => (
              <li key={`${item.sourceTaskId}|${item.nominalLocal}`} className="rounded-lg border bg-muted/40 px-3 py-2 text-sm">
                {item.title} · {item.nominalLocal.replace("T", " ")} <Badge variant="outline">Projected</Badge>
              </li>
            ))}
          </ol>
        </section>
      )}
      {isCustom && layout.kind === "board" && query.viewId && (
        <Board
          viewId={query.viewId}
          tags={tags}
          focus={focus}
          busy={busy}
          revision={revision}
          api={boardApi}
          onChanged={() => onChanged?.()}
          onOpen={(task) => onOpen(task)}
        />
      )}
      {isCustom && layout.kind === "calendar" && query.viewId && (
        <Calendar
          viewId={query.viewId}
          tags={tags}
          containers={containers ?? []}
          focus={focus}
          busy={busy}
          revision={revision}
          api={calendarApi}
          onChanged={() => onChanged?.()}
          onOpen={(task) => onOpen(task)}
        />
      )}
      {isCustom && layout.kind === "table" && rows.length > 0 && (
        <ViewTable
          rows={rows}
          columns={layout.columns}
          tags={tags}
          busy={busy}
          api={tableApi}
          tagApi={tagApi}
          revision={revision}
          onTagsChanged={() => onChanged?.()}
          selectedTaskId={selectedTaskId}
          onOpen={onOpen}
          onTaskUpdated={(task) => {
            setRows((current) => current.map((item) => (item.task.id === task.id ? { ...item, task } : item)));
            onTaskUpdated?.(task);
          }}
        />
      )}
      {!(isCustom && (layout.kind === "table" || layout.kind === "board" || layout.kind === "calendar")) && rows.length > 0 && (
        <ol className="grid gap-1" aria-label="Results">
          {shown.map((row, index) => {
            const heading = grouped && row.groupDate && row.groupDate !== shown[index - 1]?.groupDate;
            const personOf = (item: QueryRowResponse) => (query.view === "Waiting" ? item.waitingPerson : item.delegatedTo);
            const byPerson = query.view === "Waiting" || query.view === "DelegatedByMe";
            const personHeading = byPerson && (index === 0 || personOf(row) !== personOf(shown[index - 1]));
            const group = listGroup(row, layout.group, today);
            const customHeading =
              isCustom && layout.group !== "none" && (index === 0 || group.key !== listGroup(shown[index - 1], layout.group, today).key);
            const task = row.task;
            return (
              <React.Fragment key={task.id}>
                {heading && (
                  <li
                    aria-hidden="true"
                    className="flex items-center gap-1.5 border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase"
                  >
                    <CalendarDays className="size-3.5" />
                    {friendlyDate(row.groupDate, today)}
                    <span className="font-normal normal-case tracking-normal">· {row.groupDate}</span>
                  </li>
                )}
                {customHeading && (
                  <li
                    aria-hidden="true"
                    className="flex items-center gap-1.5 border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase"
                  >
                    <bdi>{group.label}</bdi>
                  </li>
                )}
                {personHeading && (
                  <li
                    aria-hidden="true"
                    className="flex items-center gap-1.5 border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase"
                  >
                    <UserRound className="size-3.5" />
                    <bdi>{personOf(row) ?? (query.view === "Waiting" ? "No person named" : "Unknown person")}</bdi>
                  </li>
                )}
                <li data-task-row={task.id}>
                  <ContextMenu.Root>
                    <ContextMenu.Trigger asChild>
                      <div
                        draggable={!busy}
                        onDragStart={(event) => {
                          event.dataTransfer.effectAllowed = "copy";
                          event.dataTransfer.setData(taskDragType, task.id);
                          event.dataTransfer.setData("text/plain", task.title);
                        }}
                        className={cn(
                          "flex min-h-11 items-center gap-2 rounded-lg border bg-card py-1.5 pr-2 pl-1.5 shadow-xs hover:border-input",
                          selectedTaskId === task.id && "border-primary ring-1 ring-primary",
                          isFinished(task.status) &&
                            "[&_[data-slot=row-title]]:text-muted-foreground [&_[data-slot=row-title]]:line-through",
                        )}
                      >
                        <CategoryEdge category={task.category} />
                        <div className="grid min-w-0 flex-1 gap-1">
                          <div className="flex items-center gap-1">
                            <button
                              type="button"
                              data-slot="row-title"
                              className="w-fit min-w-0 rounded-sm px-1 py-0.5 text-left font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50"
                              disabled={busy}
                              onClick={() => onOpen(task)}
                            >
                              {task.title}
                            </button>
                            <GitHubTaskMark taskId={task.id} version={task.version} revision={revision} />
                          </div>
                          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-xs text-muted-foreground">
                            <span className="inline-flex items-center gap-1">
                              <FolderKanban className="size-3.5" />
                              {row.containerName}
                              {row.containerArchived && " · archived"}
                            </span>
                            {task.dueDate && <DateText kind="due" date={task.dueDate} time={task.dueTime} />}
                            {task.plannedDate && <DateText kind="planned" date={task.plannedDate} time={task.plannedTime} />}
                            {query.view === "Overdue" && !task.dueDate && <Badge variant="outline">Missed recurrence</Badge>}
                            {row.waitingPerson && query.view !== "Waiting" && (
                              <span className="inline-flex items-center gap-1">
                                <UserRound className="size-3.5" />
                                Waiting on <bdi>{row.waitingPerson}</bdi>
                              </span>
                            )}
                            {row.delegatedTo && query.view !== "DelegatedByMe" && (
                              <span className="inline-flex items-center gap-1">
                                <Handshake className="size-3.5" />
                                Delegated to <bdi>{row.delegatedTo}</bdi>
                              </span>
                            )}
                            {row.nextFollowUpAtUtc && (
                              <Badge variant="outline">
                                Follow up{" "}
                                {new Date(row.nextFollowUpAtUtc).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}
                              </Badge>
                            )}
                            <StatusBadge status={task.status} />
                            <TaskStaleness task={task} lastActivityAtUtc={row.lastActivityAtUtc} revision={revision} />
                            <PriorityBadge priority={task.priority} />
                            <AssigneeChips people={assignmentInfo.chips[task.id]} />
                            {assignmentInfo.error && <span>Assignees unavailable</span>}
                            {row.matches
                              .filter((match) => match !== "Task")
                              .map((match) => (
                                <Badge key={match} variant="outline">
                                  {match === "WaitingPerson"
                                    ? "Matched the waiting person"
                                    : match === "Assignee"
                                      ? "Matched an assignee"
                                      : `Matched in ${match === "Subtask" ? "a subtask" : "the diary"}`}
                                </Badge>
                              ))}
                            {row.tagIds
                              .map((id) => byId.get(id))
                              .filter((tag): tag is TagResponse => tag !== undefined)
                              .map((tag) => (
                                <TagChip key={tag.id} tag={tag} />
                              ))}
                          </div>
                        </div>
                        <TaskViewAction task={task} title={task.title} disabled={busy} />
                      </div>
                    </ContextMenu.Trigger>
                    {onSetCategory && (
                      <ContextMenu.Portal>
                        <ContextMenu.Content
                          aria-label={`Category for ${task.title}`}
                          className="z-50 min-w-44 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-lg"
                        >
                          <CategoryMenuItems
                            category={task.category}
                            busy={busy}
                            separator={false}
                            onSelect={(category) => onSetCategory(task, category)}
                          />
                        </ContextMenu.Content>
                      </ContextMenu.Portal>
                    )}
                  </ContextMenu.Root>
                </li>
              </React.Fragment>
            );
          })}
        </ol>
      )}
      {cursor && (
        <Button type="button" variant="outline" className="justify-self-center" disabled={loading} onClick={() => void loadMore()}>
          Load more ({total - rows.length} more)
        </Button>
      )}
    </>
  );
}

/** Where a row sits under a custom list's "group by": a stable key, the heading and the heading's order. */
export function listGroup(row: QueryRowResponse, group: string, today: string): { key: string; label: string; order: number } {
  const task = row.task;
  switch (group) {
    case "container":
      return { key: row.containerName, label: row.containerName, order: 0 };
    case "status":
      return { key: task.status, label: label(task.status), order: statuses.indexOf(task.status) };
    case "priority":
      return { key: task.priority, label: task.priority, order: priorities.length - priorities.indexOf(task.priority) };
    case "due": {
      if (!task.dueDate) return { key: "none", label: "No due date", order: 4 };
      if (task.dueDate < today) return { key: "overdue", label: "Overdue", order: 0 };
      if (task.dueDate === today) return { key: "today", label: "Today", order: 1 };
      const [y, m, d] = today.split("-").map(Number);
      const week = new Date(Date.UTC(y, m - 1, d + 7)).toISOString().slice(0, 10);
      return task.dueDate <= week ? { key: "week", label: "Next 7 days", order: 2 } : { key: "later", label: "Later", order: 3 };
    }
    default:
      return { key: "", label: "", order: 0 };
  }
}
