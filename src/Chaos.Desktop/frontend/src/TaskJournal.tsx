import React from "react";
import { diaryRpc, gitHubRpc, historyRpc, type DiaryEntryResponse, type HistoryAsOfResponse, type HistoryEntryResponse, type TaskResponse, type TimelineItemResponse } from "#neoastra";
import { History, Pencil, RotateCcw, Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Count, DeletedItems, Field, StatusBadge, label } from "@/components/common";
import { MarkdownEditor, type LinkCandidate } from "./MarkdownEditor";
import { MarkdownPreview, type MarkdownLinks } from "./markdown";

type Api = { diaryRpc: typeof diaryRpc; historyRpc: typeof historyRpc };

const eventNames: Record<string, string> = {
  "task.created": "Created",
  "task.updated": "Edited",
  "task.completed": "Completed",
  "task.cancelled": "Cancelled",
  "task.reopened": "Reopened",
  "task.status-changed": "Status changed",
  "task.moved": "Moved",
  "task.reordered": "Reordered",
  "task.deleted": "Deleted",
  "task.restored": "Restored",
  "task.section-cleared": "Section removed",
  "task.tags-changed": "Tags changed",
  "subtask.added": "Subtask added",
  "subtask.updated": "Subtask edited",
  "subtask.completed": "Subtask completed",
  "subtask.cancelled": "Subtask cancelled",
  "subtask.reopened": "Subtask reopened",
  "subtask.status-changed": "Subtask status changed",
  "subtask.reordered": "Subtasks reordered",
  "subtask.deleted": "Subtask deleted",
  "subtask.restored": "Subtask restored",
  "subtask.changed": "Subtasks changed",
  "subtask.checklist-converted": "Checklist converted to subtasks",
  "diary.added": "Diary entry added",
  "diary.edited": "Diary entry edited",
  "diary.deleted": "Diary entry deleted",
  "diary.restored": "Diary entry restored",
  "diary.changed": "Diary changed",
  "undo": "Undone",
};

const fieldNames: Record<string, string> = {
  ContainerId: "list", SectionId: "section", Title: "title", Notes: "notes", Status: "status", Priority: "priority",
  EstimateMinutes: "estimate", CapacityCategory: "category", PlannedDate: "planned date", PlannedTime: "planned time",
  PlannedZoneId: "planned zone", DueDate: "due date", DueTime: "due time", DueZoneId: "due zone", Position: "order",
  DeletedAtUtc: "deletion", CompletedAtUtc: "completion", CancelledAtUtc: "cancellation", Subtasks: "subtasks", Tags: "tags", Diary: "diary",
};

/** Restorable field groups (matches the RestorableTaskField enum on the host). */
const restorable = ["Title", "Notes", "Priority", "Estimate", "Category", "Planned", "Due"] as const;

export function describeEvent(event: HistoryEntryResponse): string {
  const name = eventNames[event.eventKind] ?? label(event.eventKind);
  const fields = event.changedFields.filter(field => !["UpdatedAtUtc", "CompletedAtUtc", "CancelledAtUtc"].includes(field)).map(field => fieldNames[field] ?? field);
  return event.eventKind === "task.updated" || event.eventKind === "undo" || event.eventKind === "task.tags-changed"
    ? fields.length > 0 ? `${name}: ${[...new Set(fields)].join(", ")}` : name
    : name;
}

export const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });

function errorText(error: { message: string; fields: ReadonlyArray<{ message: string }> } | null, fallback: string) {
  return error?.fields.map(field => field.message).join("; ") || error?.message || fallback;
}

/**
 * The task's diary and history (PLAN §9): dated Markdown entries in time order alongside the
 * field-change feed, a read-only viewer for any earlier version or instant, and restoring
 * chosen values as a new recorded edit.
 */
export function TaskJournal({ task, api = { diaryRpc, historyRpc }, githubApi = gitHubRpc, revision, busy, links, searchLinks, onChanged, onError }: {
  task: TaskResponse;
  api?: Api;
  githubApi?: typeof gitHubRpc;
  revision: number;
  busy: boolean;
  links?: MarkdownLinks;
  searchLinks?: (query: string) => Promise<LinkCandidate[]>;
  onChanged: (task?: TaskResponse) => void;
  onError: (message: string) => void;
}) {
  const [items, setItems] = React.useState<TimelineItemResponse[]>([]);
  const [deleted, setDeleted] = React.useState<DiaryEntryResponse[]>([]);
  const [draft, setDraft] = React.useState("");
  const [connected, setConnected] = React.useState(false);
  const [postToGitHub, setPostToGitHub] = React.useState(false);
  const [postDefault, setPostDefault] = React.useState(false);
  const postingContext = React.useRef({ taskId: "", initialized: false });
  React.useEffect(() => {
    let active = true;
    if (postingContext.current.taskId !== task.id) {
      postingContext.current = { taskId: task.id, initialized: false };
      setConnected(false); setPostToGitHub(false); setPostDefault(false);
    }
    void githubApi.taskSync({ taskId: task.id }).then(result => {
      if (!active) return;
      const enabled = result.ok && result.connected && result.state === "Linked";
      setConnected(enabled); setPostDefault(enabled && result.postDiaryByDefault);
      const initial = !postingContext.current.initialized;
      postingContext.current.initialized = true;
      setPostToGitHub(previous => enabled && (initial ? result.postDiaryByDefault : previous));
    }).catch(() => { if (active) { setConnected(false); setPostToGitHub(false); } });
    return () => { active = false; };
  }, [githubApi, task.id, revision]);
  const [editing, setEditing] = React.useState<DiaryEntryResponse | null>(null);
  const [deleting, setDeleting] = React.useState<DiaryEntryResponse | null>(null);
  const [working, setWorking] = React.useState(false);
  const [viewer, setViewer] = React.useState<HistoryAsOfResponse | null>(null);
  const [asOf, setAsOf] = React.useState("");
  const [chosen, setChosen] = React.useState<ReadonlySet<string>>(new Set());
  const disabled = busy || working;

  async function load() {
    const [timeline, all] = await Promise.all([api.diaryRpc.timeline({ taskId: task.id }), api.diaryRpc.list({ taskId: task.id, includeDeleted: true })]);
    if (!timeline.ok) throw new Error(timeline.error?.message ?? "Could not load the diary.");
    setItems([...timeline.items]);
    if (all.ok) setDeleted(all.entries.filter(entry => entry.deleted));
  }
  React.useEffect(() => { setViewer(null); setEditing(null); setDraft(""); }, [task.id]);
  React.useEffect(() => { void load().catch((e: unknown) => onError(e instanceof Error ? e.message : String(e))); }, [task.id, task.version, revision]);

  async function mutate(action: () => Promise<{ ok: boolean; error: { message: string; fields: ReadonlyArray<{ message: string }> } | null }>, failure: string, after?: () => void) {
    if (disabled) return;
    setWorking(true);
    try {
      const result = await action();
      if (!result.ok) throw new Error(errorText(result.error, failure));
      after?.();
      await load();
      onChanged();
    } catch (e) { onError(e instanceof Error ? e.message : failure); }
    finally { setWorking(false); }
  }

  const operationId = () => crypto.randomUUID();
  function add(event: React.FormEvent) {
    event.preventDefault();
    void mutate(() => api.diaryRpc.add({ operationId: operationId(), taskId: task.id, markdown: draft, entryAtUtc: null, postToGitHub }), "Could not add the diary entry.", () => { setDraft(""); setPostToGitHub(postDefault); });
  }

  async function view(request: { entryId: string | null; atUtc: string | null }) {
    const result = await api.historyRpc.asOf({ taskId: task.id, ...request });
    if (!result.ok) { onError(result.error?.message ?? "Could not load that version."); return; }
    setChosen(new Set());
    setViewer(result);
  }

  async function restoreChosen() {
    if (!viewer?.entry || chosen.size === 0 || disabled) return;
    setWorking(true);
    try {
      const result = await api.historyRpc.restoreFields({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, entryId: viewer.entry.id, fields: [...chosen] });
      if (!result.ok || !result.task) throw new Error(errorText(result.error, "Could not restore those values."));
      setViewer(null);
      onChanged(result.task);
    } catch (e) { onError(e instanceof Error ? e.message : "Could not restore those values."); }
    finally { setWorking(false); }
  }

  const diaryCount = items.filter(item => item.diary).length;
  const snapshot = viewer?.snapshot;
  return <section className="grid gap-3" aria-labelledby="journal-heading">
    <h3 id="journal-heading" className="flex items-center font-semibold">Diary &amp; history{diaryCount > 0 && <Count>{diaryCount}</Count>}</h3>

    <form className="grid gap-2" onSubmit={add}>
      <MarkdownEditor id="diary-new" label="New diary entry" rows={3} value={draft} onChange={setDraft} disabled={disabled} placeholder="What happened? Markdown works here." links={links} searchLinks={searchLinks}/>
      {connected && <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={postToGitHub} onChange={e => setPostToGitHub(e.target.checked)} disabled={disabled}/>Also post to GitHub on sync</label>}
      <div><Button variant="outline" size="sm" disabled={disabled || !draft.trim()}>Add entry</Button></div>
    </form>

    <div className="flex items-end gap-2">
      <Field label="Show the task as of" className="flex-1"><Input type="datetime-local" value={asOf} onChange={e => setAsOf(e.target.value)}/></Field>
      <Button type="button" variant="outline" size="sm" disabled={!asOf} onClick={() => void view({ entryId: null, atUtc: new Date(asOf).toISOString() })}><History/>View</Button>
    </div>

    {viewer && <div className="grid gap-2 rounded-lg border bg-muted/50 p-3 text-sm" role="region" aria-label="Earlier version">
      <div className="flex items-center justify-between gap-2">
        <h4 className="font-semibold">{viewer.entry ? `As of ${when(viewer.entry.effectiveAtUtc)}` : "Earlier version"}</h4>
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Close earlier version" onClick={() => setViewer(null)}><X/></Button>
      </div>
      {!viewer.existed ? <p className="text-muted-foreground">This task did not exist yet.</p> : snapshot && <>
        {snapshot.deleted && <p className="font-medium text-destructive">The task was deleted at this point.</p>}
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
          <dt className="text-muted-foreground">Title</dt><dd className="[overflow-wrap:anywhere]">{snapshot.task.title}</dd>
          <dt className="text-muted-foreground">Status</dt><dd>{label(snapshot.task.status)} <StatusBadge status={snapshot.task.status}/></dd>
          <dt className="text-muted-foreground">Priority</dt><dd>{snapshot.task.priority}</dd>
          <dt className="text-muted-foreground">Planned</dt><dd>{snapshot.task.plannedDate ?? "—"}{snapshot.task.plannedTime ? ` ${snapshot.task.plannedTime}` : ""}</dd>
          <dt className="text-muted-foreground">Due</dt><dd>{snapshot.task.dueDate ?? "—"}{snapshot.task.dueTime ? ` ${snapshot.task.dueTime}` : ""}</dd>
          <dt className="text-muted-foreground">Estimate</dt><dd>{snapshot.task.estimateMinutes === null ? "—" : `${snapshot.task.estimateMinutes} min`}</dd>
          <dt className="text-muted-foreground">Subtasks</dt><dd>{snapshot.subtasks.length === 0 ? "None" : snapshot.subtasks.map(subtask => subtask.title).join(", ")}</dd>
          <dt className="text-muted-foreground">Tags</dt><dd>{snapshot.tagIds.length}</dd>
        </dl>
        <div className="grid gap-1"><span className="text-xs font-medium text-muted-foreground">Notes</span><div className="rounded-md border bg-background px-2 py-1.5"><MarkdownPreview source={snapshot.task.notes} links={links} compact/></div></div>
        {viewer.entry && <fieldset className="grid gap-2">
          <legend className="text-xs font-medium text-muted-foreground">Restore values from this version</legend>
          <div className="flex flex-wrap gap-x-3 gap-y-1">{restorable.map(field => <label key={field} className="flex items-center gap-1.5">
            <input type="checkbox" className="accent-primary" checked={chosen.has(field)} onChange={e => setChosen(current => { const next = new Set(current); if (e.target.checked) next.add(field); else next.delete(field); return next; })}/>{field}
          </label>)}</div>
          <div><Button type="button" size="sm" disabled={disabled || chosen.size === 0 || snapshot.deleted && chosen.size === 0} onClick={() => void restoreChosen()}><RotateCcw/>Restore selected</Button></div>
        </fieldset>}
      </>}
    </div>}

    {items.length > 0 && <ol className="grid gap-2" aria-label="Timeline">{[...items].reverse().map(item => item.diary
      ? <li key={`d-${item.diary.id}`} className="grid gap-1.5 rounded-lg border bg-background p-3">
          <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground">
            <span>{when(item.diary.entryAtUtc)}{item.diary.editedAtUtc && ` · edited ${when(item.diary.editedAtUtc)}`}</span>
            <span className="flex gap-0.5">
              <Button type="button" variant="ghost" size="icon-xs" aria-label="Edit diary entry" disabled={disabled} onClick={() => setEditing(item.diary)}><Pencil/></Button>
              <Button type="button" variant="ghost" size="icon-xs" className="hover:text-destructive" aria-label="Delete diary entry" disabled={disabled} onClick={() => { const entry = item.diary!; if (connected) setDeleting(entry); else void mutate(() => api.diaryRpc.delete({ operationId: operationId(), entryId: entry.id, expectedVersion: entry.version, deleteOnGitHub: false }), "Could not delete the diary entry."); }}><Trash2/></Button>
            </span>
          </div>
          {editing?.id === item.diary.id
            ? <form className="grid gap-2" onSubmit={e => { e.preventDefault(); const entry = editing; void mutate(() => api.diaryRpc.edit({ operationId: operationId(), entryId: entry.id, expectedVersion: entry.version, markdown: entry.markdown }), "Could not save the diary entry.", () => setEditing(null)); }}>
                <MarkdownEditor id={`diary-${item.diary.id}`} label="Diary entry" rows={3} value={editing.markdown} onChange={markdown => setEditing({ ...editing, markdown })} disabled={disabled} links={links} searchLinks={searchLinks}/>
                <div className="flex gap-2"><Button size="sm" disabled={disabled || !editing.markdown.trim()}>Save entry</Button><Button type="button" size="sm" variant="outline" onClick={() => setEditing(null)}>Cancel</Button></div>
              </form>
            : <MarkdownPreview source={item.diary.markdown} links={links} compact className="text-sm"/>}
        </li>
      : item.event && <li key={`e-${item.event.id}`} className="flex items-center justify-between gap-2 px-1 text-xs text-muted-foreground">
          <span><span className="font-medium text-foreground">{describeEvent(item.event)}</span> · {when(item.event.effectiveAtUtc)}</span>
          <Button type="button" variant="ghost" size="xs" onClick={() => { const event = item.event!; void view({ entryId: event.id, atUtc: null }); }}>View</Button>
        </li>)}</ol>}

    {deleting && <div role="dialog" aria-label="Delete diary entry" className="grid gap-2 rounded-lg border bg-background p-3 text-sm">
      <p>Remove this entry from Chaos, or also delete its GitHub comment if you authored it.</p>
      <div className="flex flex-wrap gap-2">{[false, true].map(remote => <Button key={String(remote)} type="button" size="sm" variant="outline" disabled={disabled} onClick={() => {
        const entry = deleting;
        void mutate(() => api.diaryRpc.delete({ operationId: operationId(), entryId: entry.id, expectedVersion: entry.version, deleteOnGitHub: remote }), "Could not delete the diary entry.", () => setDeleting(null));
      }}>{remote ? "Delete on GitHub too" : "Remove from Chaos only"}</Button>)}<Button type="button" size="sm" variant="ghost" onClick={() => setDeleting(null)}>Cancel</Button></div>
    </div>}
    <DeletedItems noun="diary entries" items={deleted} name={entry => entry.markdown.split("\n")[0].slice(0, 60)} busy={disabled}
      onRestore={entry => void mutate(() => api.diaryRpc.restore({ operationId: operationId(), entryId: entry.id, expectedVersion: entry.version, deleteOnGitHub: false }), "Could not restore the diary entry.")}/>
  </section>;
}
