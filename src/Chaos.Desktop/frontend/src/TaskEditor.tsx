import React from "react";
import { DateText, useToday } from "./dates";
import { addDays } from "./MyDay";
import { gitHubRpc, subtaskRpc, type taskRpc as TaskRpc, type containerRpc as ContainerRpc, type tagRpc as TagRpc, type markdownRpc as MarkdownRpc, type padRpc as PadRpc,
  type diaryRpc as DiaryRpc, type historyRpc as HistoryRpc, type reminderRpc as ReminderRpc, type waitingRpc as WaitingRpc, type recurrenceRpc as RecurrenceRpc,
  type ContainerResponse, type GitHubTaskSyncResponse, type PadAnchorResponse, type SectionResponse, type SubtaskOperationResponse, type SubtaskResponse, type TagResponse,
  type TaskOperationResponse, type TaskResponse } from "#neoastra";
import { ArrowDown, ArrowUp, Bell, CalendarDays, Repeat, History, ListChecks, ListTodo, NotebookPen, SlidersHorizontal, StickyNote, SunMedium, Trash2, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Count, DeletedItems, Field, FieldGroup, PriorityBadge, StatusBadge, isFinished, label, priorities, statuses } from "@/components/common";
import { GitHubTaskNotice } from "./GitHubTaskNotice";
import { MarkdownEditor, type LinkCandidate } from "./MarkdownEditor";
import { extractChecklist, MarkdownPreview, type MarkdownLinks } from "./markdown";
import { TaskJournal } from "./TaskJournal";
import { RecurrenceEditor } from "./RecurrenceEditor";
import { TaskReminders, TaskWaiting } from "./Reminders";
import { TagPicker } from "./tags";
import { ZoneSelect, type useTimeZones } from "./ZoneSelect";
import { useDragReorder } from "./dragReorder";
import { rowClass } from "./taskRow";
import { useAutosave } from "./autosave";
import type { PadFocus } from "./ScratchPad";

const operationId = () => crypto.randomUUID();
export type TaskEditorApi = {
  taskRpc: typeof TaskRpc; containerRpc: typeof ContainerRpc; tagApi: typeof TagRpc; markdownApi: typeof MarkdownRpc; padApi: typeof PadRpc;
  journalApi: { diaryRpc: typeof DiaryRpc; historyRpc: typeof HistoryRpc }; reminderApi: typeof ReminderRpc; waitingApi: typeof WaitingRpc; recurrenceApi: typeof RecurrenceRpc;
};

/**
 * The task side panel (PLAN §13): a read-only view or the editor with notes, details,
 * subtasks, diary, repeat and reminders. Field changes save themselves; subtasks, status
 * and moves save on each action as before.
 */
export function TaskEditor({ editing, setEditing, viewingTask, setViewingTask, busy, setBusy, setError, setNotice, setWarning, run, changed, refresh,
  revision, subtaskRevision, setSubtaskRevision, canAddToMyDay, onAddToMyDay, links, searchLinks, timeZones, containers, containerId, sections, tags, openPad, api }: {
  editing: TaskResponse; setEditing: React.Dispatch<React.SetStateAction<TaskResponse | null>>;
  viewingTask: boolean; setViewingTask: (viewing: boolean) => void;
  busy: boolean; setBusy: (busy: boolean) => void; setError: (error: string | null) => void; setNotice: (notice: string | null) => void; setWarning: (warning: string | null) => void;
  run: (action: () => Promise<TaskOperationResponse>, after?: (task: TaskResponse) => void) => Promise<void>;
  changed: () => void; refresh: () => Promise<void>;
  revision: number; subtaskRevision: number; setSubtaskRevision: React.Dispatch<React.SetStateAction<number>>;
  canAddToMyDay: boolean; onAddToMyDay: (task: TaskResponse) => void;
  links: MarkdownLinks; searchLinks: (text: string) => Promise<LinkCandidate[]>; timeZones: ReturnType<typeof useTimeZones>;
  containers: ContainerResponse[]; containerId: string; sections: SectionResponse[]; tags: TagResponse[];
  openPad: (request: Omit<PadFocus, "key">) => void; api: TaskEditorApi;
}) {
  const { taskRpc, containerRpc, tagApi, markdownApi, padApi, journalApi, reminderApi, waitingApi, recurrenceApi } = api;
  const drag = useDragReorder();
  const today = useToday();
  const nextMonday = addDays(today, ((8 - new Date(`${today}T00:00:00Z`).getUTCDay()) % 7) || 7);
  const quickDates: [string, string][] = [["Today", today], ["Tomorrow", addDays(today, 1)], ["Next week", nextMonday]];
  const [editorTab, setEditorTab] = React.useState("notes");
  const [checklistChoice, setChecklistChoice] = React.useState<ReadonlySet<string> | null>(null);
  const [padAnchor, setPadAnchor] = React.useState<PadAnchorResponse | null>(null);
  const [subtasks, setSubtasks] = React.useState<SubtaskResponse[]>([]);
  const [deletedSubtasks, setDeletedSubtasks] = React.useState<SubtaskResponse[]>([]);
  const [subtaskTitle, setSubtaskTitle] = React.useState("");
  const [subtaskEdit, setSubtaskEdit] = React.useState<SubtaskResponse | null>(null);
  const [githubTask, setGitHubTask] = React.useState<{ taskId: string; info: GitHubTaskSyncResponse | null } | null>(null);
  const updateGitHubStatus = React.useCallback((taskId: string, info: GitHubTaskSyncResponse | null) => {
    setGitHubTask(current => info ? { taskId, info } : current?.taskId === taskId ? current : { taskId, info: null });
  }, []);
  const taskGitHub = githubTask?.taskId === editing.id ? githubTask?.info : null;
  // A task that turns out to be GitHub-linked leaves the Repeat tab it can no longer show.
  React.useEffect(() => { if (taskGitHub?.connected && editorTab === "recurrence") setEditorTab("notes"); }, [taskGitHub?.connected, editorTab]);
  const currentEditorTaskId = React.useRef(editing.id); currentEditorTaskId.current = editing.id;

  const autosave = useAutosave<TaskResponse>({
    currentId: editing.id, onError: setError,
    validate: draft => draft.title.trim() ? null : "A task needs a title before it can be saved.",
    save: async draft => {
      const result = await taskRpc.edit({ operationId: operationId(), taskId: draft.id, expectedVersion: draft.version, title: draft.title, notes: draft.notes,
        plannedDate: draft.plannedDate || null, plannedTime: draft.plannedTime || null, plannedZoneId: draft.plannedZoneId || null,
        dueDate: draft.dueDate || null, dueTime: draft.dueTime || null, dueZoneId: draft.dueZoneId || null,
        priority: draft.priority, estimateMinutes: draft.estimateMinutes, category: draft.category });
      if (!result.ok || !result.task) return { error: result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "The change could not be saved." };
      setWarning(result.warning);
      return { saved: result.task };
    },
    onSaved: saved => {
      setEditing(current => current?.id === saved.id ? { ...current, version: saved.version, updatedAtUtc: saved.updatedAtUtc } : current);
      void refresh().catch((e: unknown) => setError(String(e)));
      changed();
    },
  });
  const subtaskAutosave = useAutosave<SubtaskResponse>({
    currentId: subtaskEdit?.id ?? null, onError: setError,
    validate: draft => draft.title.trim() ? null : "A subtask needs a title before it can be saved.",
    save: async draft => {
      const result = await subtaskRpc.edit({ operationId: operationId(), subtaskId: draft.id, expectedVersion: draft.version, title: draft.title, notes: draft.notes });
      return result.ok && result.subtask ? { saved: result.subtask } : { error: result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "The subtask could not be saved." };
    },
    onSaved: saved => {
      setSubtaskEdit(current => current?.id === saved.id ? { ...current, version: saved.version } : current);
      setSubtaskRevision(value => value + 1);
      changed();
    },
  });
  function changeSubtask(patch: Partial<SubtaskResponse>) {
    setSubtaskEdit(current => {
      if (!current) return null;
      const next = { ...current, ...patch };
      subtaskAutosave.schedule(next);
      return next;
    });
  }
  function closeSubtask() { void subtaskAutosave.flush(); setSubtaskEdit(null); }
  function change(patch: Partial<TaskResponse>) {
    setEditing(current => {
      if (!current) return null;
      const next = { ...current, ...patch };
      autosave.schedule(next);
      return next;
    });
  }
  function close() { void autosave.flush(); setEditing(null); }

  // The task editor shows where a task made from the pad came from.
  React.useEffect(() => {
    let current = true;
    setPadAnchor(null);
    void padApi.locate({ taskId: editing.id }).then(result => { if (current && result.ok) setPadAnchor(result.anchor); }).catch(() => { if (current) setPadAnchor(null); });
    return () => { current = false; };
  }, [editing.id, padApi]);
  const taskViewLinks: MarkdownLinks = React.useMemo(() => ({ ...links,
    loadGitHubImage: async (url: string) => {
      const result = await gitHubRpc.attachment({ taskId: editing.id, url });
      return result.ok && result.contentType && result.base64
        ? { dataUrl: `data:${result.contentType};base64,${result.base64}` }
        : { error: result.error?.message ?? "Could not load this GitHub attachment.", issueUrl: result.issueUrl ?? undefined };
    },
  }), [links, editing.id]);

  async function refreshSubtasks(taskId: string) {
    const response = await subtaskRpc.list({ taskId });
    if (!response.ok) throw new Error(response.error?.message ?? "Could not load subtasks.");
    return [...response.subtasks];
  }
  React.useEffect(() => { setSubtasks([]); setDeletedSubtasks([]); setSubtaskEdit(null); setSubtaskTitle(""); setChecklistChoice(null); }, [editing.id]);
  React.useEffect(() => {
    let current = true;
    void Promise.all([refreshSubtasks(editing.id), subtaskRpc.listDeleted({ taskId: editing.id })]).then(([rows, deleted]) => { if (!deleted.ok) throw new Error(deleted.error?.message ?? "Could not load deleted subtasks."); if (current) { setSubtasks(rows); setDeletedSubtasks([...deleted.subtasks]); } }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [editing.id, subtaskRevision]);

  async function runSubtask(action: () => Promise<SubtaskOperationResponse>, after?: (saved: SubtaskResponse) => void) {
    if (busy) return;
    setBusy(true); setError(null);
    try {
      const result = await action();
      if (!result.ok || !result.subtask) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "The subtask could not be saved.");
      after?.(result.subtask);
      setSubtaskRevision(current => current + 1);
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "The subtask could not be saved."); }
    finally { setBusy(false); }
  }
  function reorderSubtask(subtask: SubtaskResponse, insertionIndex: number) {
    void runSubtask(() => subtaskRpc.reorder({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, insertionIndex }));
  }
  async function convertChecklist(items: { itemKey: string; title: string; done: boolean }[]) {
    if (busy) return;
    const taskId = editing.id;
    setBusy(true); setError(null); setNotice(null);
    try {
      const result = await markdownApi.convertChecklist({ operationId: operationId(), taskId, items });
      if (!result.ok) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "Could not convert the checklist.");
      setNotice(`Created ${result.created.length} subtask${result.created.length === 1 ? "" : "s"}${result.alreadyConverted > 0 ? `; ${result.alreadyConverted} already converted` : ""}.`);
      setChecklistChoice(null);
      setSubtaskRevision(value => value + 1);
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not convert the checklist."); }
    finally { setBusy(false); }
  }
  function issueSyncFinished(taskId: string) {
    changed();
    void refresh().catch((e: unknown) => setError(String(e)));
    if (!autosave.dirty()) void taskRpc.get({ taskId }).then(result => {
      if (result.ok && result.task) setEditing(current => current?.id === taskId && !autosave.dirty() ? result.task : current);
    }).catch(() => setError("The issue synced, but its latest task details could not be loaded."));
    if (currentEditorTaskId.current === taskId) setSubtaskRevision(value => value + 1);
  }
  function rowActions(name: string, index: number, count: number, onMove: (to: number) => void, onDelete: () => void) {
  return <div className="flex gap-0.5 opacity-0 transition-opacity group-focus-within/row:opacity-100 group-hover/row:opacity-100 [@media(hover:none)]:opacity-100">
    <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy || index === 0} aria-label={`Move ${name} up`} title="Move up" onClick={() => onMove(index - 1)}><ArrowUp/></Button>
    <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy || index === count - 1} aria-label={`Move ${name} down`} title="Move down" onClick={() => onMove(index + 1)}><ArrowDown/></Button>
    <Button variant="ghost" size="icon-sm" className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" disabled={busy} aria-label={`Delete ${name}`} title="Delete" onClick={onDelete}><Trash2/></Button>
  </div>;
}

  /** Checklist-to-subtasks offer under the notes, when the saved notes contain a checklist (PLAN §9). */
function checklistPanel() {
    const checklist = extractChecklist(editing.notes);
  if (checklist.length === 0) return null;
  const chosen = checklistChoice ?? new Set(checklist.map(item => item.itemKey));
  return <details className="grid gap-2 rounded-lg border p-3 text-sm" onToggle={e => { if (!(e.currentTarget as HTMLDetailsElement).open) setChecklistChoice(null); }}>
    <summary className="flex cursor-pointer items-center gap-2 font-medium"><ListChecks className="size-4 text-muted-foreground"/>Convert checklist to subtasks ({checklist.length})</summary>
    <p className="pt-2 text-xs text-muted-foreground">Uses the saved notes. Checked items become done subtasks; the notes stay as they are. Items already converted are skipped.</p>
    <ul className="grid gap-1 pt-1" aria-label="Checklist items">{checklist.map(item => <li key={item.itemKey}><label className="flex items-center gap-2">
      <input type="checkbox" className="accent-primary" checked={chosen.has(item.itemKey)} onChange={e => { const next = new Set(chosen); if (e.target.checked) next.add(item.itemKey); else next.delete(item.itemKey); setChecklistChoice(next); }}/>
      <span className={cn(item.done && "text-muted-foreground line-through")}>{item.title}</span>
    </label></li>)}</ul>
    <div className="pt-1"><Button type="button" size="sm" variant="outline" disabled={busy || chosen.size === 0} onClick={() => void convertChecklist(checklist.filter(item => chosen.has(item.itemKey)))}>Convert {chosen.size} to subtasks</Button></div>
  </details>;
}

  // Wide windows dock the panel beside the list; narrower ones lay it over the list so the list keeps its width.
  return <aside className="fixed top-[40px] right-0 z-30 flex h-[calc(100vh-40px)] w-[min(26rem,100vw)] min-w-0 animate-in flex-col border-l bg-card shadow-2xl duration-150 fade-in slide-in-from-right-4 xl:sticky xl:z-auto xl:w-auto xl:shadow-none" aria-labelledby="editor-heading">
      <div className="flex items-center justify-between border-b py-3 pr-3 pl-5">
        <h2 id="editor-heading" className="text-xs font-semibold tracking-wider text-muted-foreground uppercase">{viewingTask ? "View task" : "Edit task"}</h2>
        <div className="flex items-center gap-1">
          {!isFinished(editing.status) && <Button type="button" variant="outline" size="sm" disabled={busy || !canAddToMyDay} title="Choose this task for today without changing its dates" onClick={() => onAddToMyDay(editing)}><SunMedium/>Add to My Day</Button>}
          <Button type="button" variant="ghost" size="icon-sm" aria-label={viewingTask ? "Close task view" : "Close task editor"} onClick={close}><X/></Button>
        </div>
      </div>
      {viewingTask ? <div className="min-h-0 flex-1 overflow-y-auto p-5">
        <h3 className="text-xl font-semibold [overflow-wrap:anywhere]">{editing.title}</h3>
        <div className="mt-3 flex flex-wrap items-center gap-2 text-sm text-muted-foreground"><StatusBadge status={editing.status}/><PriorityBadge priority={editing.priority}/><span>{editing.category}</span>{editing.estimateMinutes != null && <span>{editing.estimateMinutes} min</span>}</div>
        {(editing.plannedDate || editing.dueDate) && <div className="mt-3 grid gap-1 text-sm text-muted-foreground">{editing.plannedDate && <p><DateText kind="planned" date={editing.plannedDate} time={editing.plannedTime}/></p>}{editing.dueDate && <p><DateText kind="due" date={editing.dueDate} time={editing.dueTime}/></p>}</div>}
        <section className="mt-6"><h4 className="mb-2 font-semibold">Notes</h4>{editing.notes ? <MarkdownPreview key={editing.id} source={editing.notes} links={taskViewLinks}/> : <p className="text-sm text-muted-foreground">No notes.</p>}</section>
        {subtasks.length > 0 && <section className="mt-6"><h4 className="mb-2 font-semibold">Subtasks</h4><ul className="grid gap-3">{subtasks.map(subtask => <li key={subtask.id}><div className="flex items-center gap-2"><StatusBadge status={subtask.status}/><span className="font-medium">{subtask.title}</span></div>{subtask.notes && <MarkdownPreview source={subtask.notes} links={links} className="mt-2"/>}</li>)}</ul></section>}
        <Button className="mt-6" variant="outline" onClick={() => setViewingTask(false)}>Edit task</Button>
      </div> : <><Tabs value={editorTab} onValueChange={setEditorTab} className="min-h-0 flex-1 gap-0">
        <div className="grid gap-3 border-b px-5 pt-4 pb-3">
          <GitHubTaskNotice taskId={editing.id} version={editing.version} revision={revision + subtaskRevision} onStatus={updateGitHubStatus} onChanged={changed} onSynced={() => issueSyncFinished(editing.id)}/>
          <Field label="Title"><Input required maxLength={taskGitHub?.connected ? 256 : undefined} form="task-editor-form" className="h-10 text-sm" value={editing.title} onChange={e => change({ title: e.target.value })}/></Field>
          {/* Inactive tabs show only their icon (name in the tooltip and to screen readers), so all six fit the panel. */}
          <TabsList className="w-full [&_[data-state=inactive]_[data-slot=tab-label]]:sr-only">
            <TabsTrigger value="notes" title="Notes"><NotebookPen/><span data-slot="tab-label">Notes</span></TabsTrigger>
            <TabsTrigger value="details" title="Details"><SlidersHorizontal/><span data-slot="tab-label">Details</span></TabsTrigger>
            <TabsTrigger value="dates" title="Dates"><CalendarDays/><span data-slot="tab-label">Dates</span></TabsTrigger>
            <TabsTrigger value="subtasks" title="Subtasks"><ListTodo/><span data-slot="tab-label">Subtasks</span>{subtasks.length > 0 && <span className="text-xs text-muted-foreground tabular-nums">{subtasks.filter(s => s.status === "Done").length}/{subtasks.length}</span>}</TabsTrigger>
            <TabsTrigger value="diary" title="Diary"><History/><span data-slot="tab-label">Diary</span></TabsTrigger>
            {/* GitHub tasks cannot repeat, so their editor has no Repeat tab. */}
            {!taskGitHub?.connected && <TabsTrigger value="recurrence" title="Repeat"><Repeat/><span data-slot="tab-label">Repeat</span></TabsTrigger>}
            <TabsTrigger value="remind" title="Remind"><Bell/><span data-slot="tab-label">Remind</span></TabsTrigger>
          </TabsList>
        </div>
        <div className="min-h-0 flex-1 overflow-x-hidden overflow-y-auto p-5">
        <form id="task-editor-form" className="hidden" onSubmit={event => { event.preventDefault(); void autosave.flush(); }}/>
          <TabsContent value="notes" className="grid gap-4">
            <MarkdownEditor id="task-notes" label="Notes" value={editing.notes} onChange={notes => change({ notes })} placeholder="Add details… Markdown works here." links={links} searchLinks={searchLinks}
              areaClassName="min-h-[max(16rem,calc(100vh-24rem))]"/>
            {checklistPanel()}
            {padAnchor && <p className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
              <StickyNote className="size-3.5"/>Made from a pad note{padAnchor.deleted ? " (deleted)" : padAnchor.archived ? " (archived)" : ""}: <q className="max-w-full truncate">{padAnchor.quote}</q>
              {!padAnchor.deleted && <Button type="button" size="xs" variant="outline" onClick={() => openPad({ itemId: padAnchor.padItemId, anchor: padAnchor })}>Open in pad</Button>}
            </p>}
          </TabsContent>
          <TabsContent value="details" className="grid gap-4">
          <div className="grid grid-cols-2 gap-3">
            <Field label="Status"><NativeSelect value={editing.status} disabled={busy} onChange={e => { const status = e.target.value; const hasOpen = subtasks.some(s => !isFinished(s.status)); const choice = status === "Done" && hasOpen ? (window.confirm("Complete open subtasks too? Choose Cancel to keep them open.") ? "CompleteAll" : "KeepOpen") : null; void run(() => taskRpc.setStatus({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, status, subtaskChoice: choice }), saved => { setEditing(current => current ? { ...current, status: saved.status, version: saved.version } : null); void refreshSubtasks(saved.id).then(setSubtasks).catch((e: unknown) => setError(String(e))); }); }}>{statuses.map(s => <NativeSelectOption key={s} value={s}>{label(s)}</NativeSelectOption>)}</NativeSelect></Field>
            <Field label="Priority"><NativeSelect value={editing.priority} onChange={e => change({ priority: e.target.value })}>{priorities.map(s => <NativeSelectOption key={s}>{s}</NativeSelectOption>)}</NativeSelect></Field>
            <Field label="Category"><NativeSelect value={editing.category} onChange={e => change({ category: e.target.value })}><NativeSelectOption>Personal</NativeSelectOption><NativeSelectOption>Work</NativeSelectOption></NativeSelect></Field>
            <Field label="Estimate (minutes)"><Input type="number" min="0" value={editing.estimateMinutes ?? ""} onChange={e => change({ estimateMinutes: e.target.value === "" ? null : Number(e.target.value) })}/></Field>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Move to list or project"><NativeSelect value={editing.containerId} disabled={busy} onChange={e => { const target = e.target.value; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: target, sectionId: null }), saved => { setEditing(current => current ? { ...current, containerId: saved.containerId, sectionId: null, version: saved.version } : null); }); }}>{containers.map(c => <NativeSelectOption key={c.id} value={c.id}>{c.name}</NativeSelectOption>)}</NativeSelect></Field>
            {editing.containerId === containerId && sections.length > 0 && <Field label="Section"><NativeSelect value={editing.sectionId ?? ""} disabled={busy} onChange={e => { const sectionId = e.target.value || null; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: editing.containerId, sectionId }), saved => setEditing(current => current ? { ...current, sectionId: saved.sectionId, version: saved.version } : null)); }}><NativeSelectOption value="">No section</NativeSelectOption>{sections.map(s => <NativeSelectOption key={s.id} value={s.id}>{s.title}</NativeSelectOption>)}</NativeSelect></Field>}
          </div>
          <TagPicker targetKind="Task" targetId={editing.id} label="Tags" tags={tags} api={tagApi} revision={revision} onChanged={changed}/>
          </TabsContent>

        <TabsContent value="dates" className="grid gap-4">
          {(["planned", "due"] as const).map(kind => <FieldGroup key={kind} legend={label(kind)}>
            <fieldset disabled={kind === "due" && taskGitHub?.dueDateManaged} className="grid grid-cols-2 gap-3">
              <Field label="Date"><Input type="date" value={editing[`${kind}Date`] ?? ""} onChange={e => change(e.target.value ? { [`${kind}Date`]: e.target.value } : { [`${kind}Date`]: null, [`${kind}Time`]: null, [`${kind}ZoneId`]: null })}/></Field>
              <Field label="Time"><Input type="time" disabled={!editing[`${kind}Date`]} value={editing[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null, ...(e.target.value && !editing[`${kind}ZoneId`] ? { [`${kind}ZoneId`]: timeZones.preferred } : {}) })}/></Field>
              {editing[`${kind}Time`] && <Field label="Time zone" className="col-span-2"><ZoneSelect value={editing[`${kind}ZoneId`]} preferred={timeZones.preferred} zones={timeZones.zones} onChange={zone => change({ [`${kind}ZoneId`]: zone })}/></Field>}
              <div className="col-span-2 flex flex-wrap gap-1" role="group" aria-label={`Quick ${kind} dates`}>
                {quickDates.map(([name, date]) => <Button key={name} type="button" size="xs" variant={editing[`${kind}Date`] === date ? "secondary" : "outline"} onClick={() => change({ [`${kind}Date`]: date })}>{name}</Button>)}
                {editing[`${kind}Date`] && <Button type="button" size="xs" variant="ghost" className="text-muted-foreground" onClick={() => change({ [`${kind}Date`]: null, [`${kind}Time`]: null, [`${kind}ZoneId`]: null })}>Clear</Button>}
              </div>
            </fieldset>
            {kind === "due" && taskGitHub?.dueDateManaged && <p className="text-xs text-muted-foreground">This due date comes from the GitHub milestone. Change it on GitHub.</p>}
          </FieldGroup>)}
        </TabsContent>

        <TabsContent value="subtasks">
        <section className="grid gap-3" aria-labelledby="subtasks-heading">
          <h3 id="subtasks-heading" className="flex items-center font-semibold">Subtasks{subtasks.length > 0 && <Count>{subtasks.filter(s => s.status === "Done").length}/{subtasks.length}</Count>}</h3>
          {subtasks.length > 0 && <ol className="grid gap-1" aria-label="Subtasks">{subtasks.map((subtask, index) => <li key={subtask.id} className={rowClass(subtaskEdit?.id === subtask.id, subtask.status, true)}
            {...drag.target(subtasks.map(row => row.id), index, `subtasks|${editing.id}`, busy, (id, insertionIndex) => reorderSubtask(subtasks.find(row => row.id === id)!, insertionIndex))}>
            {drag.handle(subtask.id, `subtasks|${editing.id}`, busy)}
            <input type="checkbox" className="size-4 shrink-0 accent-primary" aria-label={`Mark ${subtask.title} as done`} checked={subtask.status === "Done"} disabled={busy} onChange={e => { const status = e.target.checked ? "Done" : "NotStarted"; void runSubtask(() => subtaskRpc.setStatus({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, status }), saved => setSubtaskEdit(current => current?.id === saved.id ? { ...current, status: saved.status, version: saved.version } : current)); }}/>
            <div className="min-w-0 flex-1"><button data-slot="row-title" className="w-fit rounded-sm px-1 py-0.5 text-left [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={busy} onClick={() => setSubtaskEdit(subtask)}>{subtask.title}</button></div>
            <StatusBadge status={subtask.status}/>
            {rowActions(subtask.title, index, subtasks.length, to => reorderSubtask(subtask, to), () => void runSubtask(() => subtaskRpc.delete({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, title: subtask.title, notes: subtask.notes }), () => { if (subtaskEdit?.id === subtask.id) setSubtaskEdit(null); }))}
          </li>)}</ol>}
          <form className="flex gap-2" onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.add({ operationId: operationId(), taskId: editing.id, title: subtaskTitle, notes: "" }), () => setSubtaskTitle("")); }}>
            <label htmlFor="new-subtask" className="sr-only">New subtask</label>
            <Input id="new-subtask" className="border-dashed bg-transparent focus-visible:border-solid" value={subtaskTitle} onChange={e => setSubtaskTitle(e.target.value)} placeholder="Add a subtask" autoComplete="off"/>
            <Button variant="outline" disabled={busy || !subtaskTitle.trim()}>Add subtask</Button>
          </form>
          {subtaskEdit && <Card className="gap-4 py-4"><CardContent className="px-4"><form className="grid gap-4" onSubmit={e => { e.preventDefault(); void subtaskAutosave.flush(); }}>
            <h4 className="font-semibold">Edit subtask</h4>
            <Field label="Title"><Input required value={subtaskEdit.title} onChange={e => changeSubtask({ title: e.target.value })}/></Field>
            <MarkdownEditor id="subtask-notes" label="Subtask notes" rows={3} value={subtaskEdit.notes} onChange={notes => changeSubtask({ notes })} links={links} searchLinks={searchLinks}/>
            <Field label="Status"><NativeSelect value={subtaskEdit.status} disabled={busy} onChange={e => { const status = e.target.value; void subtaskAutosave.flush().then(() => runSubtask(() => subtaskRpc.setStatus({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, status }), saved => setSubtaskEdit(current => current ? { ...current, status: saved.status, version: saved.version } : null))); }}>{statuses.map(s => <NativeSelectOption key={s} value={s}>{label(s)}</NativeSelectOption>)}</NativeSelect></Field>
            <div className="flex items-center gap-2">
              <p role="status" aria-live="polite" className={cn("text-xs text-muted-foreground", subtaskAutosave.state === "error" && "text-destructive")}>{subtaskAutosave.label}</p>
              {subtaskAutosave.state === "error" && <Button type="button" size="sm" variant="outline" onClick={() => void subtaskAutosave.flush()}>Retry</Button>}
              <Button type="button" variant="outline" className="ml-auto" onClick={closeSubtask}>Done</Button>
            </div>
          </form></CardContent></Card>}
          {subtaskEdit && <TagPicker targetKind="Subtask" targetId={subtaskEdit.id} label={`Tags on “${subtaskEdit.title}”`} tags={tags} api={tagApi} revision={revision} onChanged={changed}/>}
          <DeletedItems noun="subtasks" items={deletedSubtasks} name={subtask => subtask.title} busy={busy} onRestore={subtask => void runSubtask(() => subtaskRpc.restore({ operationId: operationId(), taskId: editing.id, subtaskId: subtask.id, expectedVersion: subtask.version }))}/>
        </section>
        </TabsContent>

        <TabsContent value="diary">
        <TaskJournal task={editing} api={journalApi} revision={revision + subtaskRevision} busy={busy} links={links} searchLinks={searchLinks}
          onError={setError}
          onChanged={saved => { if (saved) { setEditing(saved); void refresh().catch((e: unknown) => setError(String(e))); } changed(); }}/>
        </TabsContent>
        <TabsContent value="remind" className="grid gap-6">
          <TaskReminders task={editing} revision={revision} busy={busy} api={reminderApi} onChanged={changed}/>
          <TaskWaiting task={editing} revision={revision} busy={busy} api={waitingApi}
            onChanged={saved => { if (saved) setEditing(current => current?.id === saved.id ? { ...current, status: saved.status, version: saved.version } : current); changed(); }}/>
        </TabsContent>
        <TabsContent value="recurrence">{taskGitHub?.connected ? <p className="text-sm text-muted-foreground">GitHub tasks cannot repeat. Keep this as an ordinary task to add recurrence.</p> : <RecurrenceEditor task={editing} timeZones={timeZones} revision={revision} busy={busy} onError={setError} taskApi={taskRpc} api={recurrenceApi}
          onChanged={saved => { setEditing(current => current?.id === saved.id ? { ...current, status: saved.status, version: saved.version, plannedDate: saved.plannedDate, plannedTime: saved.plannedTime, plannedZoneId: saved.plannedZoneId, dueDate: saved.dueDate, dueTime: saved.dueTime, dueZoneId: saved.dueZoneId } : current); void refresh().catch((e: unknown) => setError(String(e))); changed(); }}/>}</TabsContent>
        </div>
      </Tabs>
      <div className="flex items-center gap-2 border-t px-5 py-3">
        <p role="status" aria-live="polite" className={cn("text-xs text-muted-foreground", autosave.state === "error" && "text-destructive")}>{autosave.label}</p>
        {autosave.state === "error" && <Button type="button" size="sm" variant="outline" onClick={() => void autosave.flush()}>Retry</Button>}
        <Button type="button" variant="outline" className="ml-auto" onClick={close}>Done</Button>
      </div></>}
    </aside>;
}
