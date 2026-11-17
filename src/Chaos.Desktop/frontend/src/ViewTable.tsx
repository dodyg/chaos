import React from "react";
import { taskRpc, tagRpc, type QueryRowResponse, type TagResponse, type TaskResponse } from "#neoastra";
import { ArrowDown, ArrowUp } from "lucide-react";
import { cn } from "@/lib/utils";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { InlineError, PriorityBadge, StatusBadge, isFinished, label, priorities, statuses } from "@/components/common";
import { TagChip, TagPicker } from "./tags";
import { tableColumns } from "./viewModel";

export type TableTaskApi = Pick<typeof taskRpc, "edit" | "setStatus">;

type Props = {
  rows: readonly QueryRowResponse[];
  columns: readonly string[];
  tags: readonly TagResponse[];
  busy: boolean;
  api?: TableTaskApi;
  selectedTaskId?: string | null;
  /** Inline tag editing: the tag service, the revision that reloads a picker, and what to do after a tag change (reload the rows and counts). */
  tagApi?: typeof tagRpc; revision?: number; onTagsChanged?: () => void;
  onOpen: (task: TaskResponse) => void;
  /** A committed inline edit: the row's task is replaced and the sidebar counts reload. */
  onTaskUpdated: (task: TaskResponse) => void;
};

const operationId = () => crypto.randomUUID();
const columnLabel = (name: string) => tableColumns.find(column => column.name === name)?.label ?? name;
const priorityRank = (value: string) => priorities.indexOf(value);

/** The sortable value of one cell; empty values sort last in either direction. */
function sortValue(row: QueryRowResponse, column: string): string | number | null {
  const task = row.task;
  switch (column) {
    case "title": return task.title.toLocaleLowerCase();
    case "status": return statuses.indexOf(task.status);
    case "priority": return priorityRank(task.priority);
    case "container": return row.containerName.toLocaleLowerCase();
    case "planned": return task.plannedDate ? `${task.plannedDate} ${task.plannedTime ?? ""}` : null;
    case "due": return task.dueDate ? `${task.dueDate} ${task.dueTime ?? ""}` : null;
    case "estimate": return task.estimateMinutes;
    case "created": return task.createdAtUtc;
    case "changed": return task.updatedAtUtc;
    default: return null;
  }
}

/**
 * The table layout of a custom view (PLAN2 §11.2): the chosen columns, sortable headers and inline
 * edit of title, status, priority, planned, due and estimate. Every edit is the ordinary task
 * command with the row's version, so a stale row is refused rather than overwritten. Header sorting
 * orders the rows already loaded; the view's saved sort decides which rows come first.
 */
export function ViewTable({ rows, columns, tags, busy, api = taskRpc, selectedTaskId = null, tagApi = tagRpc, revision = 0, onTagsChanged, onOpen, onTaskUpdated }: Props) {
  const [sort, setSort] = React.useState<{ column: string; descending: boolean } | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState<string | null>(null);
  const byId = new Map(tags.map(tag => [tag.id, tag]));

  const ordered = React.useMemo(() => {
    if (!sort) return [...rows];
    return [...rows].sort((a, b) => {
      const left = sortValue(a, sort.column), right = sortValue(b, sort.column);
      if (left === null && right === null) return 0;
      if (left === null) return 1;
      if (right === null) return -1;
      const result = left < right ? -1 : left > right ? 1 : 0;
      return sort.descending ? -result : result;
    });
  }, [rows, sort]);

  async function edit(task: TaskResponse, patch: Partial<Pick<TaskResponse, "title" | "priority" | "plannedDate" | "dueDate" | "estimateMinutes">>) {
    if (saving) return;
    setSaving(task.id); setError(null);
    try {
      const next = { ...task, ...patch };
      const result = await api.edit({
        operationId: operationId(), taskId: task.id, expectedVersion: task.version, title: next.title, notes: next.notes,
        plannedDate: next.plannedDate, plannedTime: next.plannedDate ? next.plannedTime : null, plannedZoneId: next.plannedDate ? next.plannedZoneId : null,
        dueDate: next.dueDate, dueTime: next.dueDate ? next.dueTime : null, dueZoneId: next.dueDate ? next.dueZoneId : null,
        priority: next.priority, estimateMinutes: next.estimateMinutes, category: next.category,
      });
      if (!result.ok || !result.task) { setError(result.error?.message ?? "The task could not be changed."); return; }
      onTaskUpdated(result.task);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(null); }
  }

  async function setStatus(task: TaskResponse, status: string) {
    if (saving || status === task.status) return;
    setSaving(task.id); setError(null);
    try {
      const result = await api.setStatus({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, status, subtaskChoice: null });
      if (!result.ok || !result.task) { setError(result.error?.message ?? "The status could not be changed."); return; }
      onTaskUpdated(result.task);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(null); }
  }

  const disabled = (task: TaskResponse) => busy || saving === task.id;
  return <div className="grid gap-2">
    {error && <InlineError>{error}</InlineError>}
    <div className="overflow-x-auto rounded-xl border">
      <table className="w-full min-w-max border-collapse text-sm" aria-label="Tasks">
        <thead className="bg-muted/50 text-left">
          <tr>{columns.map(column => {
            const sorted = sort?.column === column;
            return <th key={column} scope="col" aria-sort={sorted ? (sort.descending ? "descending" : "ascending") : "none"} className="px-3 py-2 font-semibold">
              {column === "tags" ? columnLabel(column)
                : <button type="button" className="inline-flex items-center gap-1 rounded-sm outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50"
                  onClick={() => setSort(current => current?.column === column ? (current.descending ? null : { column, descending: true }) : { column, descending: false })}>
                  {columnLabel(column)}{sorted && (sort.descending ? <ArrowDown className="size-3.5" aria-hidden="true"/> : <ArrowUp className="size-3.5" aria-hidden="true"/>)}</button>}
            </th>;
          })}</tr>
        </thead>
        <tbody>{ordered.map(row => {
          const task = row.task;
          return <tr key={task.id} data-task-row={task.id} className={cn("border-t align-middle", selectedTaskId === task.id && "bg-primary/5", isFinished(task.status) && "text-muted-foreground")}>
            {columns.map(column => <td key={column} className="px-3 py-1.5">
              <Cell column={column} row={row} byId={byId} disabled={disabled(task)} onOpen={onOpen}
                onTitle={title => void edit(task, { title })} onStatus={status => void setStatus(task, status)} onPriority={priority => void edit(task, { priority })}
                onPlanned={date => void edit(task, { plannedDate: date || null })} onDue={date => void edit(task, { dueDate: date || null })}
                onEstimate={minutes => void edit(task, { estimateMinutes: minutes })}
                tags={tags} tagApi={tagApi} revision={revision} onTagsChanged={() => onTagsChanged?.()}/>
            </td>)}
          </tr>;
        })}</tbody>
      </table>
    </div>
  </div>;
}

function Cell({ column, row, byId, disabled, onOpen, onTitle, onStatus, onPriority, onPlanned, onDue, onEstimate, tags, tagApi, revision, onTagsChanged }: {
  tags: readonly TagResponse[]; tagApi: typeof tagRpc; revision: number; onTagsChanged: () => void;
  column: string; row: QueryRowResponse; byId: ReadonlyMap<string, TagResponse>; disabled: boolean; onOpen: (task: TaskResponse) => void;
  onTitle: (title: string) => void; onStatus: (status: string) => void; onPriority: (priority: string) => void;
  onPlanned: (date: string) => void; onDue: (date: string) => void; onEstimate: (minutes: number | null) => void;
}) {
  const task = row.task;
  switch (column) {
    case "title": return <TitleCell task={task} disabled={disabled} onOpen={onOpen} onCommit={onTitle}/>;
    case "status": return <NativeSelect aria-label={`Status of ${task.title}`} className="h-8 w-48" value={task.status} disabled={disabled} onChange={event => onStatus(event.target.value)}>
      {statuses.map(status => <NativeSelectOption key={status} value={status}>{label(status)}</NativeSelectOption>)}</NativeSelect>;
    case "priority": return <NativeSelect aria-label={`Priority of ${task.title}`} className="h-8 w-28" value={task.priority} disabled={disabled} onChange={event => onPriority(event.target.value)}>
      {priorities.map(priority => <NativeSelectOption key={priority} value={priority}>{priority}</NativeSelectOption>)}</NativeSelect>;
    case "planned": return <Input aria-label={`Planned date of ${task.title}`} type="date" className="h-8 w-40" value={task.plannedDate ?? ""} disabled={disabled} onChange={event => onPlanned(event.target.value)}/>;
    case "due": return <Input aria-label={`Due date of ${task.title}`} type="date" className="h-8 w-40" value={task.dueDate ?? ""} disabled={disabled} onChange={event => onDue(event.target.value)}/>;
    case "estimate": return <EstimateCell task={task} disabled={disabled} onCommit={onEstimate}/>;
    case "container": return <bdi>{row.containerName}</bdi>;
    case "tags": return <TagsCell row={row} byId={byId} tags={tags} api={tagApi} revision={revision} disabled={disabled} onChanged={onTagsChanged}/>;
    case "created": return <time dateTime={task.createdAtUtc}>{new Date(task.createdAtUtc).toLocaleDateString()}</time>;
    case "changed": return <time dateTime={task.updatedAtUtc}>{new Date(task.updatedAtUtc).toLocaleDateString()}</time>;
    case "completed": return task.status === "Done" ? <time dateTime={task.updatedAtUtc}>{new Date(task.updatedAtUtc).toLocaleDateString()}</time> : null;
    default: return null;
  }
}

/** The title is a link to the task until it is double-clicked or Enter is pressed on its edit button. */
function TitleCell({ task, disabled, onOpen, onCommit }: { task: TaskResponse; disabled: boolean; onOpen: (task: TaskResponse) => void; onCommit: (title: string) => void }) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(task.title);
  React.useEffect(() => { setDraft(task.title); }, [task.title]);
  function commit() {
    setEditing(false);
    const title = draft.trim();
    if (title && title !== task.title) onCommit(title); else setDraft(task.title);
  }
  if (editing) return <Input aria-label={`Title of ${task.title}`} autoFocus dir="auto" className="h-8 min-w-56" value={draft} maxLength={500}
    onChange={event => setDraft(event.target.value)} onBlur={commit}
    onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); commit(); } else if (event.key === "Escape") { setEditing(false); setDraft(task.title); } }}/>;
  return <span className="flex items-center gap-2">
    <button type="button" data-slot="row-title" className="min-w-0 rounded-sm text-left font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={disabled} onClick={() => onOpen(task)}>{task.title}</button>
    <button type="button" className="text-xs text-muted-foreground underline-offset-2 hover:underline focus-visible:underline" disabled={disabled} aria-label={`Edit title of ${task.title}`} onClick={() => setEditing(true)}>Edit</button>
    <span className="sr-only"><StatusBadge status={task.status}/><PriorityBadge priority={task.priority}/></span>
  </span>;
}

function EstimateCell({ task, disabled, onCommit }: { task: TaskResponse; disabled: boolean; onCommit: (minutes: number | null) => void }) {
  const [draft, setDraft] = React.useState(task.estimateMinutes === null ? "" : String(task.estimateMinutes));
  React.useEffect(() => { setDraft(task.estimateMinutes === null ? "" : String(task.estimateMinutes)); }, [task.estimateMinutes]);
  function commit() {
    const minutes = draft.trim() === "" ? null : Number(draft);
    if (minutes !== null && (!Number.isInteger(minutes) || minutes < 0 || minutes > 10080)) { setDraft(task.estimateMinutes === null ? "" : String(task.estimateMinutes)); return; }
    if (minutes !== task.estimateMinutes) onCommit(minutes);
  }
  return <Input aria-label={`Estimate in minutes of ${task.title}`} type="number" min={0} max={10080} className="h-8 w-24" value={draft} disabled={disabled}
    onChange={event => setDraft(event.target.value)} onBlur={commit} onKeyDown={event => { if (event.key === "Enter") commit(); }}/>;
}

/** The tags of a row as chips; Edit opens the same tag picker the task editor uses (suggestions, nested paths) for this one task. */
function TagsCell({ row, byId, tags, api, revision, disabled, onChanged }: {
  row: QueryRowResponse; byId: ReadonlyMap<string, TagResponse>; tags: readonly TagResponse[]; api: typeof tagRpc; revision: number; disabled: boolean; onChanged: () => void;
}) {
  const [editing, setEditing] = React.useState(false);
  const task = row.task;
  if (editing) return <span className="grid min-w-56 gap-1">
    <TagPicker targetKind="Task" targetId={task.id} label={`Tags on ${task.title}`} tags={[...tags]} api={api} revision={revision} onChanged={onChanged}/>
    <button type="button" className="w-fit text-xs text-muted-foreground underline-offset-2 hover:underline" onClick={() => setEditing(false)}>Done editing tags</button>
  </span>;
  return <span className="flex flex-wrap items-center gap-1">
    {row.tagIds.map(id => byId.get(id)).filter((tag): tag is TagResponse => tag !== undefined).map(tag => <TagChip key={tag.id} tag={tag}/>)}
    <button type="button" className="text-xs text-muted-foreground underline-offset-2 hover:underline focus-visible:underline" disabled={disabled} aria-label={`Edit tags of ${task.title}`} onClick={() => setEditing(true)}>Edit</button>
  </span>;
}
