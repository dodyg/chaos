import React from "react";
import { taskRpc, recurrenceRpc, subtaskRpc, containerRpc, tagRpc, queryRpc, undoRpc, markdownRpc, diaryRpc, historyRpc, padRpc, captureRpc, myDayRpc, myWeekRpc, reminderRpc, waitingRpc, journalRpc, type CaptureDraftDto, type PadAnchorResponse, type PadItemResponse, type TaskResponse, type TaskOperationResponse, type SubtaskResponse, type SubtaskOperationResponse, type ContainerResponse, type SectionResponse, type TagResponse, type UndoEntryResponse } from "#neoastra";
import { MarkdownEditor, type LinkCandidate } from "./MarkdownEditor";
import { TaskJournal } from "./TaskJournal";
import { RecurrenceEditor } from "./RecurrenceEditor";
import { extractChecklist, type MarkdownLinks } from "./markdown";
import { Templates } from "./Templates";
import { ContainerManagement } from "./ContainerManagement";
import { createLatestRequest } from "./latestRequest";
import { useDragReorder } from "./dragReorder";
import { QueryView, builtInViews, type QueryState } from "./QueryView";
import { NewTagForm, TagPicker, TagSettings, TagTree, taskDragType } from "./tags";
import { ScratchPad, padExcerpt, type PadFocus } from "./ScratchPad";
import { DraftRegistry } from "./padAutosave";
import { QuickAdd } from "./QuickAdd";
import { MyDay, useDayClock } from "./MyDay";
import { MyWeek } from "./MyWeek";
import { AlertStack, NotificationCenter, TaskReminders, TaskWaiting } from "./Reminders";
import { Journal, SummaryPrompts } from "./Journal";
import { DataSettings } from "./DataSettings";
import { AlarmClock, Archive, Bell, Database, BookOpen, Sun, SunMedium, ArchiveRestore, ArrowDown, ArrowUp, CalendarCheck, CalendarDays, CalendarRange, CircleCheckBig, Clock, FolderKanban, History, Hourglass, Inbox, LayoutTemplate, ListChecks, ListTodo, NotebookPen, Search, Settings2, SlidersHorizontal, StickyNote, Trash2, Undo2, UserRound, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Banner, Count, DeletedItems, EmptyState, Field, FieldGroup, PageHeader, PriorityBadge, StatusBadge, isFinished, label, priorities, statuses } from "@/components/common";

const viewIcons: Record<string, React.ReactNode> = { Today: <CalendarCheck/>, Upcoming: <CalendarRange/>, Overdue: <AlarmClock/>, Waiting: <Hourglass/>, RecentlyCompleted: <CircleCheckBig/>, AssignedToMe: <UserRound/> };

type Api = { taskRpc: typeof taskRpc; containerRpc: typeof containerRpc; recurrenceRpc?: typeof recurrenceRpc; tagRpc?: typeof tagRpc; queryRpc?: typeof queryRpc; undoRpc?: typeof undoRpc; markdownRpc?: typeof markdownRpc; diaryRpc?: typeof diaryRpc; historyRpc?: typeof historyRpc; padRpc?: typeof padRpc; captureRpc?: typeof captureRpc; myDayRpc?: typeof myDayRpc; myWeekRpc?: typeof myWeekRpc; reminderRpc?: typeof reminderRpc; waitingRpc?: typeof waitingRpc; journalRpc?: typeof journalRpc };

/** Focus is in a text field, where Ctrl+Z belongs to the field's own text undo (PLAN §9). */
function isTextEditing(target: EventTarget | null) {
  const element = target as HTMLElement | null;
  return !!element && (element.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(element.tagName));
}

export function App({ api = { taskRpc, containerRpc }, templates }: { api?: Api; templates?: React.ReactNode }) {
  const { taskRpc, containerRpc } = api;
  const tagApi = api.tagRpc ?? tagRpc;
  const queryApi = api.queryRpc ?? queryRpc;
  const undoApi = api.undoRpc ?? undoRpc;
  const markdownApi = api.markdownRpc ?? markdownRpc;
  const journalApi = { diaryRpc: api.diaryRpc ?? diaryRpc, historyRpc: api.historyRpc ?? historyRpc };
  const padApi = api.padRpc ?? padRpc;
  const recurrenceApi = api.recurrenceRpc ?? recurrenceRpc;
  const captureApi = api.captureRpc ?? captureRpc;
  const myDayApi = api.myDayRpc ?? myDayRpc;
  const myWeekApi = api.myWeekRpc ?? myWeekRpc;
  const reminderApi = api.reminderRpc ?? reminderRpc;
  const waitingApi = api.waitingRpc ?? waitingRpc;
  const summaryApi = api.journalRpc ?? journalRpc;
  const dayClock = useDayClock(myDayApi);
  const quickAddInput = React.useRef<HTMLTextAreaElement>(null);
  const drafts = React.useRef(new DraftRegistry()).current;
  const [padFocus, setPadFocus] = React.useState<PadFocus | null>(null);
  const [padAnchor, setPadAnchor] = React.useState<PadAnchorResponse | null>(null);
  const [padMatches, setPadMatches] = React.useState<PadItemResponse[]>([]);
  const [lastUndo, setLastUndo] = React.useState<UndoEntryResponse | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [checklistChoice, setChecklistChoice] = React.useState<ReadonlySet<string> | null>(null);
  const [editorTab, setEditorTab] = React.useState("notes");
  const [tasks, setTasks] = React.useState<TaskResponse[]>([]);
  const [editing, setEditing] = React.useState<TaskResponse | null>(null);
  const [recurrenceDelete, setRecurrenceDelete] = React.useState<TaskResponse | null>(null);
  const [recurrenceConflict, setRecurrenceConflict] = React.useState<{ taskId: string; message: string } | null>(null);
  const [deleted, setDeleted] = React.useState<TaskResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [warning, setWarning] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [subtasks, setSubtasks] = React.useState<SubtaskResponse[]>([]);
  const [deletedSubtasks, setDeletedSubtasks] = React.useState<SubtaskResponse[]>([]);
  const [subtaskTitle, setSubtaskTitle] = React.useState("");
  const [subtaskEdit, setSubtaskEdit] = React.useState<SubtaskResponse | null>(null);
  const [containers, setContainers] = React.useState<ContainerResponse[]>([]);
  const [deletedContainers, setDeletedContainers] = React.useState<ContainerResponse[]>([]);
  const [containerId, setContainerId] = React.useState("");
  const [containerName, setContainerName] = React.useState("");
  const [containerKind, setContainerKind] = React.useState("List");
  const [sections, setSections] = React.useState<SectionResponse[]>([]);
  const [sectionTitle, setSectionTitle] = React.useState("");
  const [subtaskRevision, setSubtaskRevision] = React.useState(0);
  const [view, setView] = React.useState<"tasks" | "templates" | "query" | "pad" | "myday" | "myweek" | "notifications" | "journal" | "data">("tasks");
  const [journalOpenId, setJournalOpenId] = React.useState<string | null>(null);
  const [query, setQuery] = React.useState<QueryState>({ view: "Today" });
  const [searchText, setSearchText] = React.useState("");
  const [tags, setTags] = React.useState<TagResponse[]>([]);
  const [deletedTags, setDeletedTags] = React.useState<TagResponse[]>([]);
  const [viewCounts, setViewCounts] = React.useState<ReadonlyMap<string, number>>(new Map());
  const [tagCounts, setTagCounts] = React.useState<ReadonlyMap<string, number>>(new Map());
  const [revision, setRevision] = React.useState(0);
  const [showTagSettings, setShowTagSettings] = React.useState(false);
  const [showSettings, setShowSettings] = React.useState(false);
  const listRequests = React.useRef(createLatestRequest());
  const selectedContainer = React.useRef("");
  const drag = useDragReorder();

  async function refresh() {
    if (containerId !== selectedContainer.current) return;
    const isCurrent = listRequests.current.begin();
    try {
      const response = containerId ? await containerRpc.tasks({ containerId }) : await taskRpc.listInbox({});
      if (!response.ok) throw new Error(response.error?.message ?? "Could not load the list.");
      const removed = await taskRpc.listDeleted({ containerId: containerId || null });
      if (!removed.ok) throw new Error(removed.error?.message ?? "Could not load deleted tasks.");
      if (isCurrent()) { setTasks([...response.tasks]); setDeleted([...removed.tasks]); }
    } catch (error) { if (isCurrent()) throw error; }
  }
  function selectContainer(id: string) {
    listRequests.current.invalidate();
    selectedContainer.current = id;
    setTasks([]); setDeleted([]); setSections([]); setEditing(null); setShowSettings(false); setContainerId(id);
  }
  async function refreshContainers() {
    const response = await containerRpc.list({});
    if (!response.ok) throw new Error(response.error?.message ?? "Could not load lists.");
    const removed = await containerRpc.listDeleted({});
    if (!removed.ok) throw new Error(removed.error?.message ?? "Could not load deleted lists.");
    setContainers([...response.containers]);
    setDeletedContainers([...removed.containers]);
  }
  /** Tags, deleted tags and sidebar counts; counts use the same query as each view (PLAN §10). */
  async function refreshMeta() {
    const [list, removed, counts] = await Promise.all([tagApi.list({}), tagApi.listDeleted({}), queryApi.counts({})]);
    if (!list.ok) throw new Error(list.error?.message ?? "Could not load tags.");
    setTags([...list.tags]);
    if (removed.ok) setDeletedTags([...removed.tags]);
    if (counts.ok) {
      setViewCounts(new Map(counts.views.map(entry => [entry.view, entry.count])));
      setTagCounts(new Map(counts.tags.map(entry => [entry.tagId, entry.count])));
    }
  }
  /** After any committed change: reload the open query view, the sidebar counts and the undo offer. */
  function changed() {
    setRevision(current => current + 1);
    void refreshMeta().catch((e: unknown) => setError(String(e)));
    void undoApi.list({ limit: 1 }).then(result => { if (result.ok) setLastUndo(result.entries[0] ?? null); }).catch(() => setLastUndo(null));
  }
  // A new local date (midnight, resume after sleep, zone change) reloads views and counts without a restart.
  const shownDate = React.useRef<string | null>(null);
  React.useEffect(() => {
    if (!dayClock) return;
    if (shownDate.current !== null && shownDate.current !== dayClock.today) changed();
    shownDate.current = dayClock.today;
  }, [dayClock?.today]);
  /** Adds the open task to today's My Day; its planned and due dates stay as they are. */
  async function addToMyDay(task: TaskResponse) {
    if (busy || !dayClock) return;
    setBusy(true); setError(null); setNotice(null);
    try {
      const result = await myDayApi.add({ operationId: operationId(), date: dayClock.today, taskId: task.id });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not add the task to My Day.");
      setNotice(result.changed === 0 ? `“${task.title}” is already on My Day.` : `Added “${task.title}” to My Day.`);
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not add the task to My Day."); }
    finally { setBusy(false); }
  }
  /** Operation-level undo of the newest change; on a conflict nothing changes and the reason is shown. */
  async function undoLatest() {
    if (busy) return;
    setBusy(true); setError(null); setNotice(null);
    try {
      const latest = (await undoApi.list({ limit: 1 })).entries[0];
      if (!latest) { setNotice("There is nothing to undo."); return; }
      const result = await undoApi.undo({ operationId: operationId(), targetOperationId: latest.operationId });
      if (!result.ok) {
        const successor = result.error?.fields.find(field => field.fieldPath === "recurrenceSuccessorId");
        if (successor) { setRecurrenceConflict({ taskId: successor.message, message: result.error?.message ?? "Review the successor before undoing." }); return; }
        throw new Error(result.error?.message ?? "Could not undo that change.");
      }
      setNotice(`Undid ${result.label}.`);
      await Promise.all([refresh(), refreshContainers()]);
      if (editing) {
        const current = await taskRpc.get({ taskId: editing.id });
        setEditing(current.ok && current.task ? current.task : null);
        if (current.ok && current.task) setSubtasks(await refreshSubtasks(current.task.id));
      }
      setSubtaskRevision(value => value + 1);
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not undo that change."); }
    finally { setBusy(false); }
  }
  const undoRef = React.useRef(undoLatest);
  undoRef.current = undoLatest;
  React.useEffect(() => {
    function key(event: KeyboardEvent) {
      if ((event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === "z" && !isTextEditing(event.target)) {
        event.preventDefault();
        void undoRef.current();
      }
      // The Personal-pad shortcut (PLAN §2 capture surface): a new note from anywhere in the app.
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && event.key.toLowerCase() === "p") {
        event.preventDefault();
        openPadRef.current({ itemId: null });
      }
      // Quick add from anywhere in the app (PLAN §13 Ctrl+N).
      if ((event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === "n") {
        event.preventDefault();
        focusQuickAddRef.current();
      }
    }
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, []);
  /** Shows the pad: a note (optionally at a backlink's anchor) or a new note. */
  function openPad(request: Omit<PadFocus, "key">) {
    setShowTagSettings(false);
    setView("pad");
    setPadFocus(current => ({ ...request, key: (current?.key ?? 0) + 1 }));
  }
  const openPadRef = React.useRef(openPad);
  openPadRef.current = openPad;
  function focusQuickAdd() {
    setShowTagSettings(false);
    if (view !== "tasks") { setEditing(null); setView("tasks"); }
    setTimeout(() => quickAddInput.current?.focus(), 0);
  }
  const focusQuickAddRef = React.useRef(focusQuickAdd);
  focusQuickAddRef.current = focusQuickAdd;
  /** Saves confirmed quick-add drafts as one operation; the list and counts reload after commit. */
  async function saveCaptured(captured: CaptureDraftDto[], operation: string): Promise<string | null> {
    const result = await captureApi.create({ operationId: operation, drafts: captured });
    if (!result.ok) return result.error?.fields.map(field => field.message).join("; ") || result.error?.message || "The tasks could not be saved.";
    setNotice(result.tasks.length === 1 ? null : `Added ${result.tasks.length} tasks.`);
    await refresh();
    changed();
    return null;
  }
  // Leaving the pad saves its notes at once; a failure keeps the text and says where it is.
  const previousView = React.useRef(view);
  React.useEffect(() => {
    if (previousView.current === "pad" && view !== "pad") {
      void drafts.flushAll().then(ok => { if (!ok) setError("A pad note could not be saved. Its text is kept: open the Personal pad to retry or copy it."); });
    }
    previousView.current = view;
  }, [view]);
  // Closing or hiding the window saves unsaved pad text now instead of after the idle delay.
  React.useEffect(() => {
    function hidden() { if (document.visibilityState === "hidden" && drafts.dirty) void drafts.flushAll(); }
    function closing(event: BeforeUnloadEvent) {
      if (!drafts.dirty) return;
      void drafts.flushAll();
      event.preventDefault();
      event.returnValue = "";
    }
    document.addEventListener("visibilitychange", hidden);
    window.addEventListener("beforeunload", closing);
    return () => { document.removeEventListener("visibilitychange", hidden); window.removeEventListener("beforeunload", closing); };
  }, [drafts]);
  // The task editor shows where a task made from the pad came from.
  React.useEffect(() => {
    let current = true;
    setPadAnchor(null);
    if (editing?.id) void padApi.locate({ taskId: editing.id }).then(result => { if (current && result.ok) setPadAnchor(result.anchor); }).catch(() => { if (current) setPadAnchor(null); });
    return () => { current = false; };
  }, [editing?.id, padApi]);
  // Search also covers typed pad text (PLAN §9).
  React.useEffect(() => {
    let current = true;
    setPadMatches([]);
    if (view === "query" && query.view === "Search" && query.text?.trim()) {
      void padApi.search({ text: query.text, limit: 20 }).then(result => { if (current && result.ok) setPadMatches([...result.items]); }).catch(() => { if (current) setPadMatches([]); });
    }
    return () => { current = false; };
  }, [view, query.view, query.text, revision, padApi]);
  async function openTaskById(taskId: string) {
    try {
      const result = await taskRpc.get({ taskId });
      if (result.ok && result.task) setEditing(result.task); else setError(result.error?.message ?? "Could not open the task.");
    } catch (e) { setError(String(e)); }
  }

  async function openLinkedTask(taskId: string) {
    const result = await taskRpc.get({ taskId });
    if (result.ok && result.task) setEditing(result.task);
    else setError(result.error?.message ?? "That task is not available.");
  }
  const links: MarkdownLinks = React.useMemo(() => ({
    resolve: async (taskIds: string[]) => { const result = await markdownApi.resolveLinks({ taskIds }); return result.ok ? [...result.targets] : []; },
    onOpenTask: (taskId: string) => void openLinkedTask(taskId),
    onOpenExternal: (url: string) => void markdownApi.openExternal({ url }).then(result => { if (!result.ok) setError(result.error?.message ?? "Could not open that link."); }),
    resolvePads: async (itemIds: string[]) => { const result = await padApi.resolveLinks({ itemIds }); return result.ok ? [...result.targets] : []; },
    onOpenPad: (itemId: string) => openPadRef.current({ itemId }),
  }), [markdownApi, padApi]);
  const searchLinks = React.useCallback(async (text: string): Promise<LinkCandidate[]> => {
    if (!text.trim()) return [];
    const [result, notes] = await Promise.all([
      queryApi.run({ view: "Search", text, containerId: null, tagId: null, statuses: null, priorities: null, plannedFrom: null, plannedTo: null, dueFrom: null, dueTo: null, completedFrom: null, completedTo: null, includeCompleted: true, includeArchived: false, upcomingDays: null, limit: 8, cursor: null, day: null }),
      padApi.search({ text, limit: 4 }).catch(() => null),
    ]);
    const tasks: LinkCandidate[] = result.ok ? result.rows.map(row => ({ id: row.task.id, title: row.task.title })) : [];
    const pads: LinkCandidate[] = notes?.ok ? notes.items.map(item => ({ id: item.id, title: padExcerpt(item.markdown), kind: "pad" as const })) : [];
    return [...tasks.slice(0, 6), ...pads];
  }, [queryApi, padApi]);
  async function convertChecklist(items: { itemKey: string; title: string; done: boolean }[]) {
    if (busy || !editing) return;
    const taskId = editing.id;
    setBusy(true); setError(null); setNotice(null);
    try {
      const result = await markdownApi.convertChecklist({ operationId: operationId(), taskId, items });
      if (!result.ok) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "Could not convert the checklist.");
      setNotice(`Created ${result.created.length} subtask${result.created.length === 1 ? "" : "s"}${result.alreadyConverted > 0 ? `; ${result.alreadyConverted} already converted` : ""}.`);
      setSubtasks(await refreshSubtasks(taskId));
      setChecklistChoice(null);
      setSubtaskRevision(value => value + 1);
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not convert the checklist."); }
    finally { setBusy(false); }
  }
  React.useEffect(() => { void refreshContainers().catch((e: unknown) => setError(String(e))); }, []);
  React.useEffect(() => { void refreshMeta().catch((e: unknown) => setError(String(e))); }, []);
  React.useEffect(() => { void refresh().catch((e: unknown) => setError(String(e))); }, [containerId]);
  React.useEffect(() => {
    let active = true;
    if (!containerId) { setSections([]); return; }
    void containerRpc.sections({ containerId }).then(result => { if (active && result.ok) setSections([...result.sections]); else if (active) setError(result.error?.message ?? "Could not load sections."); }).catch((e: unknown) => { if (active) setError(String(e)); });
    return () => { active = false; };
  }, [containerId]);
  async function refreshSubtasks(taskId: string) {
    const response = await subtaskRpc.list({ taskId });
    if (!response.ok) throw new Error(response.error?.message ?? "Could not load subtasks.");
    return [...response.subtasks];
  }
  React.useEffect(() => {
    let current = true;
    setSubtasks([]); setDeletedSubtasks([]); setSubtaskEdit(null); setSubtaskTitle("");
    if (editing?.id) void Promise.all([refreshSubtasks(editing.id), subtaskRpc.listDeleted({ taskId: editing.id })]).then(([rows, deleted]) => { if (!deleted.ok) throw new Error(deleted.error?.message ?? "Could not load deleted subtasks."); if (current) { setSubtasks(rows); setDeletedSubtasks([...deleted.subtasks]); } }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [editing?.id]);

  async function runSubtask(action: () => Promise<SubtaskOperationResponse>, after?: (saved: SubtaskResponse) => void) {
    if (busy || !editing) return;
    const taskId = editing.id;
    setBusy(true); setError(null);
    try {
      const result = await action();
      if (!result.ok || !result.subtask) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "The subtask could not be saved.");
      after?.(result.subtask);
      const rows = await refreshSubtasks(taskId);
      setSubtasks(rows);
      const deleted = await subtaskRpc.listDeleted({ taskId });
      if (!deleted.ok) throw new Error(deleted.error?.message ?? "Could not load deleted subtasks.");
      setDeletedSubtasks([...deleted.subtasks]);
      setSubtaskRevision(current => current + 1);
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "The subtask could not be saved."); }
    finally { setBusy(false); }
  }

  async function run(action: () => Promise<TaskOperationResponse>, after?: (task: TaskResponse) => void) {
    if (busy) return;
    setBusy(true); setError(null); setWarning(null);
    try {
      const result = await action();
      if (!result.ok || !result.task) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "The change could not be saved.");
      setWarning(result.warning);
      after?.(result.task);
      await refresh();
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "The change could not be saved."); }
    finally { setBusy(false); }
  }
  const operationId = () => crypto.randomUUID();
  function save(event: React.FormEvent) {
    event.preventDefault();
    if (!editing) return;
    const t = editing;
    void run(() => taskRpc.edit({ operationId: operationId(), taskId: t.id, expectedVersion: t.version, title: t.title, notes: t.notes,
      plannedDate: t.plannedDate || null, plannedTime: t.plannedTime || null, plannedZoneId: t.plannedZoneId || null,
      dueDate: t.dueDate || null, dueTime: t.dueTime || null, dueZoneId: t.dueZoneId || null,
      priority: t.priority, estimateMinutes: t.estimateMinutes, category: t.category }), () => setEditing(null));
  }
  function change(patch: Partial<TaskResponse>) { setEditing(t => t ? { ...t, ...patch } : null); }
  async function requestTaskDeletion(task: TaskResponse) {
    try {
      const result = await recurrenceApi.get({ taskId: task.id });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not inspect recurrence.");
      if (result.rule && !result.ended && result.occurrences.some(item => item.taskId === task.id && item.state === "Active")) {
        setRecurrenceDelete(task); return;
      }
      await run(() => taskRpc.delete({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); });
    } catch (error) { setError(error instanceof Error ? error.message : String(error)); }
  }
  function reorderTask(task: TaskResponse, insertionIndex: number) {
    void run(() => taskRpc.reorder({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, insertionIndex }));
  }
  function reorderSubtask(subtask: SubtaskResponse, insertionIndex: number) {
    void runSubtask(() => subtaskRpc.reorder({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, insertionIndex }));
  }
  async function runContainer(action: () => Promise<{ ok: boolean; error: { message: string } | null }>, failure: string) {
    if (busy) return;
    setBusy(true); setError(null);
    try { const result = await action(); if (!result.ok) throw new Error(result.error?.message ?? failure); await refreshContainers(); changed(); }
    catch (e) { setError(e instanceof Error ? e.message : failure); }
    finally { setBusy(false); }
  }
  function openContainer(id: string) {
    if (view === "query") setEditing(null);
    setView("tasks");
    if (id !== containerId) selectContainer(id);
  }
  function openQuery(next: QueryState) {
    setEditing(null); setShowTagSettings(false);
    setQuery(next); setView("query");
  }
  function search(event: React.FormEvent) {
    event.preventDefault();
    if (searchText.trim()) openQuery({ view: "Search", text: searchText.trim() });
  }
  async function createTag(path: string) {
    if (busy) return false;
    setBusy(true); setError(null);
    try {
      const result = await tagApi.create({ operationId: operationId(), path, parentId: null, color: null });
      if (!result.ok) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "Could not create the tag.");
      await refreshMeta();
      return true;
    } catch (e) { setError(e instanceof Error ? e.message : "Could not create the tag."); return false; }
    finally { setBusy(false); }
  }
  /** Drag-to-tag (PLAN §6): adds the tag without moving the task. */
  async function tagTask(taskId: string, tag: TagResponse) {
    if (busy) return;
    setBusy(true); setError(null);
    try {
      const result = await tagApi.link({ operationId: operationId(), targetKind: "Task", targetId: taskId, tagId: tag.id, path: null });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not tag the task.");
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not tag the task."); }
    finally { setBusy(false); }
  }
  async function restoreTag(tag: TagResponse) {
    if (busy) return;
    setBusy(true); setError(null);
    try {
      const result = await tagApi.restore({ operationId: operationId(), tagId: tag.id, expectedVersion: tag.version });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not restore the tag.");
      changed();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not restore the tag."); }
    finally { setBusy(false); }
  }
  function createContainer(event: React.FormEvent) {
    event.preventDefault();
    void (async () => {
      setBusy(true); setError(null);
      try {
        const result = await containerRpc.create({ operationId: operationId(), name: containerName, kind: containerKind });
        if (!result.ok || !result.container) throw new Error(result.error?.message ?? "Could not create list.");
        await refreshContainers(); setView("tasks"); selectContainer(result.container.id); setContainerName("");
      } catch (err) { setError(String(err)); } finally { setBusy(false); }
    })();
  }
  function createSection(event: React.FormEvent) {
    event.preventDefault();
    void (async () => {
      setBusy(true);
      try {
        const result = await containerRpc.createSection({ operationId: operationId(), containerId, title: sectionTitle });
        if (!result.ok) throw new Error(result.error?.message ?? "Could not create section.");
        const list = await containerRpc.sections({ containerId });
        if (!list.ok) throw new Error(list.error?.message ?? "Could not load sections.");
        setSections([...list.sections]); setSectionTitle("");
      } catch (err) { setError(String(err)); } finally { setBusy(false); }
    })();
  }
  /** Checklist-to-subtasks offer under the notes, when the saved notes contain a checklist (PLAN §9). */
  function checklistPanel() {
    if (!editing) return null;
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
  function taskMeta(task: TaskResponse) {
    const parts: React.ReactNode[] = [];
    if (task.status !== "NotStarted") parts.push(<StatusBadge key="status" status={task.status}/>);
    if (task.dueDate) parts.push(<span key="due" className="inline-flex items-center gap-1"><CalendarDays className="size-3.5"/>Due {task.dueDate}{task.dueTime ? ` ${task.dueTime}` : ""}</span>);
    else if (task.plannedDate) parts.push(<span key="planned" className="inline-flex items-center gap-1"><CalendarDays className="size-3.5"/>Planned {task.plannedDate}</span>);
    if (task.estimateMinutes) parts.push(<span key="estimate" className="inline-flex items-center gap-1"><Clock className="size-3.5"/>{task.estimateMinutes} min</span>);
    if (task.priority === "High" || task.priority === "Urgent") parts.push(<PriorityBadge key="priority" priority={task.priority}/>);
    return parts;
  }
  function rowClass(selected: boolean, status: string, compact = false) {
    return cn("group/row flex items-center gap-2 rounded-lg border bg-card py-1 pr-1.5 pl-1 shadow-xs transition-colors hover:border-input",
      compact ? "min-h-9" : "min-h-11",
      selected && "border-primary ring-1 ring-primary hover:border-primary",
      isFinished(status) && "[&_[data-slot=row-title]]:text-muted-foreground [&_[data-slot=row-title]]:line-through");
  }
  function rowActions(name: string, index: number, count: number, onMove: (to: number) => void, onDelete: () => void) {
    return <div className="flex gap-0.5 opacity-0 transition-opacity group-focus-within/row:opacity-100 group-hover/row:opacity-100 [@media(hover:none)]:opacity-100">
      <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy || index === 0} aria-label={`Move ${name} up`} title="Move up" onClick={() => onMove(index - 1)}><ArrowUp/></Button>
      <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy || index === count - 1} aria-label={`Move ${name} down`} title="Move down" onClick={() => onMove(index + 1)}><ArrowDown/></Button>
      <Button variant="ghost" size="icon-sm" className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" disabled={busy} aria-label={`Delete ${name}`} title="Delete" onClick={onDelete}><Trash2/></Button>
    </div>;
  }
  function taskList(rows: TaskResponse[], name: string) {
    const order = rows.map(row => row.id);
    const byId = (id: string) => rows.find(row => row.id === id)!;
    return <ol className="grid gap-1" aria-label={name}>{rows.map((task, index) => <li key={task.id} className={rowClass(editing?.id === task.id, task.status)}
      onDragStart={event => event.dataTransfer?.setData?.(taskDragType, task.id)}
      {...drag.target(order, index, `${task.containerId}|${task.sectionId ?? ""}`, busy, (id, insertionIndex) => reorderTask(byId(id), insertionIndex))}>
      {drag.handle(task.id, `${task.containerId}|${task.sectionId ?? ""}`, busy)}
      <div className="grid min-w-0 flex-1 gap-0.5">
        <button data-slot="row-title" className="w-fit rounded-sm px-1 py-0.5 text-left font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={busy} onClick={() => setEditing(task)}>{task.title}</button>
        {taskMeta(task).length > 0 && <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-xs text-muted-foreground">{taskMeta(task)}</div>}
      </div>
      {rowActions(task.title, index, rows.length, to => reorderTask(task, to), () => void requestTaskDeletion(task))}
    </li>)}</ol>;
  }
  function groupHeading(text: string, count?: number) {
    return <h3 className="flex items-center border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">{text}{count !== undefined && <Count>{count}</Count>}</h3>;
  }
  const unsectioned = tasks.filter(task => task.sectionId === null);
  const knownSectionIds = new Set(sections.map(section => section.id));
  const unknownSections = [...new Set(tasks.map(task => task.sectionId).filter((id): id is string => id !== null && !knownSectionIds.has(id)))];
  const selected = containers.find(c => c.id === containerId);
  const listName = selected?.name ?? "Inbox";
  const openCount = tasks.filter(t => !isFinished(t.status)).length;
  const userContainers = containers.filter(c => c.kind !== "Inbox");
  const navItem = "h-8 min-w-0 flex-1 justify-start gap-2.5 px-2 font-normal text-sidebar-foreground hover:bg-sidebar-accent disabled:opacity-100 [&_svg]:text-muted-foreground aria-[current=page]:bg-brand-soft aria-[current=page]:font-semibold aria-[current=page]:text-primary aria-[current=page]:[&_svg]:text-primary";
  const showDetail = editing !== null && view !== "templates";
  const activeTag = view === "query" && query.view === "Tag" ? tags.find(tag => tag.id === query.tagId) ?? null : null;
  const builtIn = builtInViews.find(item => item.view === query.view);
  const queryHeader = query.view === "Search"
    ? <><PageHeader eyebrow="Search" title={`“${query.text ?? ""}”`} subtitle="Matches words in titles, notes, subtasks, diary entries and pad notes. Completed tasks and archived lists need a filter."/>
      {padMatches.length > 0 && <section aria-label="Matching pad notes" className="grid gap-1.5">
        <h3 className="flex items-center border-b px-1 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">Pad notes<Count>{padMatches.length}</Count></h3>
        <ul className="grid gap-1">{padMatches.map(item => <li key={item.id}><button type="button" className="flex w-full items-center gap-2 rounded-lg border bg-card px-3 py-2 text-left text-sm hover:border-input" onClick={() => openPad({ itemId: item.id })}>
          <StickyNote className="size-4 shrink-0 text-muted-foreground"/><span className="truncate">{padExcerpt(item.markdown)}</span>{item.archived && <span className="ml-auto text-xs text-muted-foreground">archived</span>}
        </button></li>)}</ul>
      </section>}</>
    : query.view === "Tag"
      ? <><PageHeader eyebrow="Tag" title={activeTag ? `#${activeTag.path}` : "Tag"} subtitle="Open tasks with this tag or a tag nested inside it, including tagged subtasks."
          actions={activeTag && <Button type="button" variant={showTagSettings ? "secondary" : "outline"} aria-expanded={showTagSettings} onClick={() => setShowTagSettings(v => !v)}><Settings2/>{showTagSettings ? "Hide settings" : "Settings"}</Button>}/>
        {activeTag && showTagSettings && <div className="rounded-xl border bg-muted/50 p-4"><TagSettings tag={activeTag} tags={tags} api={tagApi}
          onChanged={async () => { await refreshMeta(); setRevision(current => current + 1); }}
          onDeleted={async () => { await refreshMeta(); openQuery({ view: "Today" }); }}/></div>}</>
      : <PageHeader eyebrow="View" title={builtIn?.title ?? query.view} subtitle={builtIn?.hint ?? ""}/>;

  return <div className={cn("grid min-h-screen grid-cols-[15.5rem_minmax(0,1fr)] overflow-x-clip", showDetail && "grid-cols-[15.5rem_minmax(0,1fr)_minmax(21rem,26rem)]")} aria-busy={busy}>
    <aside className="sticky top-0 flex h-screen flex-col gap-1 overflow-y-auto border-r border-sidebar-border bg-sidebar px-3 py-4 text-sidebar-foreground">
      <h1 className="flex items-center gap-2 px-2 pb-3 text-lg font-bold tracking-tight"><span className="text-xl leading-none text-primary" aria-hidden="true">✷</span>Chaos</h1>
      <form role="search" className="mb-2 flex items-center gap-1.5 rounded-md border border-input bg-background px-2 focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/30" onSubmit={search}>
        <Search className="size-4 shrink-0 text-muted-foreground"/>
        <label htmlFor="search" className="sr-only">Search tasks</label>
        <input id="search" type="search" className="h-8 min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground" value={searchText} onChange={e => setSearchText(e.target.value)} placeholder="Search tasks" autoComplete="off"/>
      </form>
      <nav aria-label="Views" className="grid gap-px">
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "myday" ? "page" : undefined} onClick={() => { setEditing(null); setShowTagSettings(false); setView("myday"); }}>
          <Sun/>My Day{(viewCounts.get("MyDay") ?? 0) > 0 && <span className="ml-auto text-xs text-muted-foreground tabular-nums">{viewCounts.get("MyDay")}</span>}
        </Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "myweek" ? "page" : undefined} onClick={() => { setEditing(null); setShowTagSettings(false); setView("myweek"); }}><CalendarDays/>My Week</Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "tasks" && !containerId ? "page" : undefined} onClick={() => openContainer("")}><Inbox/>Inbox</Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "pad" ? "page" : undefined} title="Personal pad (Ctrl+Shift+P for a new note)" onClick={() => { setShowTagSettings(false); setView("pad"); }}><StickyNote/>Personal pad</Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "journal" ? "page" : undefined} onClick={() => { setShowTagSettings(false); setView("journal"); }}><BookOpen/>Journal</Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "notifications" ? "page" : undefined} onClick={() => { setShowTagSettings(false); setView("notifications"); }}><Bell/>Notifications</Button>
        {builtInViews.map(item => <Button key={item.view} type="button" variant="ghost" className={navItem} aria-current={view === "query" && query.view === item.view ? "page" : undefined} onClick={() => openQuery({ view: item.view })}>
          {viewIcons[item.view]}{item.title}{(viewCounts.get(item.view) ?? 0) > 0 && <span className="ml-auto text-xs text-muted-foreground tabular-nums">{viewCounts.get(item.view)}</span>}
        </Button>)}
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "templates" ? "page" : undefined} onClick={() => { setEditing(null); setView("templates"); }}><LayoutTemplate/>Templates</Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "data" ? "page" : undefined} onClick={() => { setEditing(null); setShowTagSettings(false); setView("data"); }}><Database/>Data &amp; backups</Button>
      </nav>
      <div className="mx-2 mt-4 mb-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">Lists &amp; projects</div>
      {userContainers.length > 0
        ? <nav aria-label="Lists and projects"><ol className="grid gap-px">{containers.map((c, index) => c.kind === "Inbox" ? null : <li key={c.id} className="group/row flex items-center rounded-md"
            {...drag.target(containers.map(row => row.id), index, "containers", busy, (id, insertionIndex) => { const moving = containers.find(row => row.id === id)!; void runContainer(() => containerRpc.reorder({ operationId: operationId(), containerId: moving.id, expectedVersion: moving.version, insertionIndex }), "Could not reorder lists."); })}>
            {drag.handle(c.id, "containers", busy, "w-0 overflow-hidden p-0 group-focus-within/row:w-auto group-focus-within/row:px-0.5 group-hover/row:w-auto group-hover/row:px-0.5")}
            <Button type="button" variant="ghost" className={cn(navItem, c.archived && "text-muted-foreground")} disabled={busy} aria-current={view === "tasks" && c.id === containerId ? "page" : undefined} onClick={() => openContainer(c.id)}>{c.kind === "Project" ? <FolderKanban/> : <ListTodo/>}<span className="truncate">{c.name}</span></Button>
            {c.archived && <span className="pr-2 text-[0.7rem] text-muted-foreground">archived</span>}
          </li>)}</ol></nav>
        : <p className="px-2 py-1 text-muted-foreground">No lists yet. Create one below.</p>}
      <form className="mt-2 grid gap-2 rounded-lg border border-dashed border-input p-2" onSubmit={createContainer}>
        <label className="sr-only" htmlFor="new-container">New list or project</label>
        <Input id="new-container" placeholder="New list or project" className="bg-background" value={containerName} onChange={e => setContainerName(e.target.value)} autoComplete="off"/>
        <div className="flex gap-2">
          <label className="sr-only" htmlFor="new-container-kind">Type</label>
          <div className="flex-1 [&_[data-slot=native-select-wrapper]]:w-full"><NativeSelect id="new-container-kind" className="bg-background" value={containerKind} onChange={e => setContainerKind(e.target.value)}><NativeSelectOption>List</NativeSelectOption><NativeSelectOption>Project</NativeSelectOption></NativeSelect></div>
          <Button disabled={busy || !containerName.trim()}>Create</Button>
        </div>
      </form>
      <div className="mx-2 mt-4 mb-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">Tags</div>
      <TagTree tags={tags} counts={tagCounts} activeTagId={activeTag?.id ?? null} busy={busy} onOpen={tag => openQuery({ view: "Tag", tagId: tag.id })} onTagTask={(taskId, tag) => void tagTask(taskId, tag)}/>
      <NewTagForm busy={busy} onCreate={createTag}/>
      <DeletedItems className="pt-2" noun="tags" items={deletedTags} name={tag => tag.name} busy={busy} onRestore={tag => void restoreTag(tag)}/>
      <DeletedItems className="mt-auto pt-4" noun="lists" items={deletedContainers} name={item => item.name} busy={busy} onRestore={item => void runContainer(() => containerRpc.restore({ operationId: operationId(), containerId: item.id, expectedVersion: item.version, name: item.name }), "Could not restore list.")}/>
    </aside>

    <div className="grid w-full max-w-4xl min-w-0 content-start gap-5 justify-self-center px-[clamp(1rem,4vw,3rem)] pt-8 pb-16">
      {error && <Banner tone="error" onDismiss={() => setError(null)}>{error}</Banner>}
      {warning && <Banner tone="warning" onDismiss={() => setWarning(null)}>{warning}</Banner>}
      {notice && <Banner tone="warning" onDismiss={() => setNotice(null)}>{notice}</Banner>}
      <SummaryPrompts api={summaryApi} onOpen={id => { setJournalOpenId(id); setShowTagSettings(false); setView("journal"); }}/>

      {view === "pad" ? <ScratchPad api={padApi} tagApi={tagApi} captureApi={captureApi} registry={drafts} containers={containers} tags={tags} revision={revision} focus={padFocus}
          links={links} searchLinks={searchLinks} onChanged={changed} onOpenTask={taskId => void openLinkedTask(taskId)} onError={setError}/>
      : view === "myweek" ? <MyWeek clock={dayClock} revision={revision} busy={busy} containers={containers} api={myWeekApi} onOpen={task => setEditing(task)} onChanged={changed}/>
      : view === "myday" ? <MyDay clock={dayClock} revision={revision} selectedTaskId={editing?.id ?? null} busy={busy} api={myDayApi} queryApi={queryApi} waitingApi={waitingApi} onOpen={task => setEditing(task)} onChanged={changed}/>
      : view === "journal" ? <Journal api={summaryApi} revision={revision} today={dayClock?.today ?? ""} openId={journalOpenId} onOpenTask={id => void openTaskById(id)} onChanged={changed}/>
      : view === "data" ? <DataSettings containers={containers} onImported={() => { void refreshMeta().catch((e: unknown) => setError(String(e))); changed(); }} onReplaced={() => { setEditing(null); void refreshMeta().catch((e: unknown) => setError(String(e))); changed(); }}/>
      : view === "notifications" ? <NotificationCenter revision={revision} api={reminderApi} onOpen={id => void openTaskById(id)}/>
      : view === "query" ? <QueryView query={query} tags={tags} revision={revision} selectedTaskId={editing?.id ?? null} busy={busy} api={queryApi} recurrenceApi={recurrenceApi} onOpen={task => setEditing(task)} header={queryHeader}/>
      : view === "templates" ? (templates ?? <Templates containerId={containerId} containerName={listName} onCreated={refresh}/>) : <>
        <PageHeader eyebrow={`${selected ? selected.kind : "Inbox"}${selected?.archived ? " · archived" : ""}${selected?.deadlineDate ? ` · due ${selected.deadlineDate}` : ""}`} title={listName}
          subtitle={tasks.length === 0 ? "Nothing here yet" : `${openCount} open · ${tasks.length - openCount} finished`}
          actions={selected && <>
            <Button type="button" variant="outline" disabled={busy} onClick={() => { const request = { operationId: operationId(), containerId: selected.id, expectedVersion: selected.version, name: selected.name }; void runContainer(() => selected.archived ? containerRpc.unarchive(request) : containerRpc.archive(request), "Could not update archive state."); }}>{selected.archived ? <ArchiveRestore/> : <Archive/>}{selected.archived ? "Unarchive" : "Archive"}</Button>
            <Button type="button" variant={showSettings ? "secondary" : "outline"} aria-expanded={showSettings} aria-controls="list-settings" onClick={() => setShowSettings(v => !v)}><Settings2/>{showSettings ? "Hide settings" : "Settings"}</Button>
          </>}/>

        {selected && showSettings && <div id="list-settings" className="rounded-xl border bg-muted/50 p-4"><ContainerManagement container={selected} containers={containers} sections={sections} taskRevision={`${subtaskRevision}|${tasks.map(t => `${t.id}:${t.version}`).join("|")}`} onDeleted={async () => { if (selectedContainer.current === containerId) selectContainer(""); await refreshContainers(); }} onChanged={async () => { await refreshContainers(); const result = await containerRpc.sections({ containerId }); if (selectedContainer.current !== containerId) return; if (!result.ok) throw new Error(result.error?.message ?? "Could not load sections."); setSections([...result.sections]); await refresh(); }}/></div>}

        <QuickAdd key={containerId} api={captureApi} containers={containers} defaultContainerId={containerId} label="Add a task" inputRef={quickAddInput}
          placeholder={`Add a task to ${listName} — e.g. Call plumber tomorrow 3pm #home`} onSubmit={saveCaptured}/>

        {tasks.length === 0 && <EmptyState title="No tasks in this list.">Type above and press Enter to capture one. Ctrl+N jumps here from anywhere.</EmptyState>}
        {unsectioned.length > 0 && <section className="grid gap-1.5" aria-label="Unsectioned tasks">{sections.length > 0 && groupHeading("Unsectioned")}{taskList(unsectioned, "Unsectioned tasks")}</section>}
        {sections.map(section => { const rows = tasks.filter(task => task.sectionId === section.id); return <section key={section.id} className="grid gap-1.5" aria-label={`${section.title} tasks`}>{groupHeading(section.title, rows.length)}{rows.length === 0 ? <p className="px-2 py-1 text-muted-foreground italic">No tasks in this section.</p> : taskList(rows, `${section.title} tasks`)}</section>; })}
        {unknownSections.map(id => <section key={id} className="grid gap-1.5" aria-label="Other section tasks">{groupHeading("Other section")}{taskList(tasks.filter(task => task.sectionId === id), "Other section tasks")}</section>)}

        {containerId && <form className="flex gap-2" onSubmit={createSection}>
          <label htmlFor="new-section" className="sr-only">New section</label>
          <Input id="new-section" className="border-dashed bg-transparent focus-visible:border-solid" value={sectionTitle} onChange={e => setSectionTitle(e.target.value)} placeholder="New section" autoComplete="off"/>
          <Button variant="outline" disabled={busy || !sectionTitle.trim()}>Add section</Button>
        </form>}

        <DeletedItems noun="tasks" items={deleted} name={task => task.title} busy={busy} onRestore={task => void run(() => taskRpc.restore({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }))}/>
      </>}
    </div>

    {editing && view !== "templates" && <aside className="sticky top-0 flex h-screen min-w-0 animate-in flex-col border-l bg-card duration-150 fade-in slide-in-from-right-4" aria-labelledby="editor-heading">
      <div className="flex items-center justify-between border-b py-3 pr-3 pl-5">
        <h2 id="editor-heading" className="text-xs font-semibold tracking-wider text-muted-foreground uppercase">Edit task</h2>
        <div className="flex items-center gap-1">
          {!isFinished(editing.status) && <Button type="button" variant="outline" size="sm" disabled={busy || !dayClock} title="Choose this task for today without changing its dates" onClick={() => void addToMyDay(editing)}><SunMedium/>Add to My Day</Button>}
          <Button type="button" variant="ghost" size="icon-sm" aria-label="Close task editor" onClick={() => setEditing(null)}><X/></Button>
        </div>
      </div>
      <Tabs value={editorTab} onValueChange={setEditorTab} className="min-h-0 flex-1 gap-0">
        <div className="grid gap-3 border-b px-5 pt-4 pb-3">
          <Field label="Title"><Input required form="task-editor-form" className="h-10 text-base font-semibold md:text-base" value={editing.title} onChange={e => change({ title: e.target.value })}/></Field>
          <TabsList className="w-full">
            <TabsTrigger value="notes"><NotebookPen/>Notes</TabsTrigger>
            <TabsTrigger value="details"><SlidersHorizontal/>Details</TabsTrigger>
            <TabsTrigger value="subtasks"><ListTodo/>Subtasks{subtasks.length > 0 && <span className="text-xs text-muted-foreground tabular-nums">{subtasks.filter(s => s.status === "Done").length}/{subtasks.length}</span>}</TabsTrigger>
            <TabsTrigger value="diary"><History/>Diary</TabsTrigger>
            <TabsTrigger value="recurrence"><CalendarRange/>Repeat</TabsTrigger>
            <TabsTrigger value="remind"><Bell/>Remind</TabsTrigger>
          </TabsList>
        </div>
        <div className="min-h-0 flex-1 overflow-x-hidden overflow-y-auto p-5">
        <form id="task-editor-form" className="hidden" onSubmit={save}/>
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
          {(["planned", "due"] as const).map(kind => <FieldGroup key={kind} legend={label(kind)}>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Date"><Input type="date" value={editing[`${kind}Date`] ?? ""} onChange={e => change({ [`${kind}Date`]: e.target.value || null })}/></Field>
              <Field label="Time"><Input type="time" value={editing[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null })}/></Field>
              <Field label="Time zone" className="col-span-2"><Input value={editing[`${kind}ZoneId`] ?? ""} onChange={e => change({ [`${kind}ZoneId`]: e.target.value || null })} placeholder="e.g. Africa/Cairo"/></Field>
            </div>
          </FieldGroup>)}
          <div className="grid grid-cols-2 gap-3">
            <Field label="Move to list or project"><NativeSelect value={editing.containerId} disabled={busy} onChange={e => { const target = e.target.value; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: target, sectionId: null }), saved => { setEditing(current => current ? { ...current, containerId: saved.containerId, sectionId: null, version: saved.version } : null); }); }}>{containers.map(c => <NativeSelectOption key={c.id} value={c.id}>{c.name}</NativeSelectOption>)}</NativeSelect></Field>
            {editing.containerId === containerId && sections.length > 0 && <Field label="Section"><NativeSelect value={editing.sectionId ?? ""} disabled={busy} onChange={e => { const sectionId = e.target.value || null; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: editing.containerId, sectionId }), saved => setEditing(current => current ? { ...current, sectionId: saved.sectionId, version: saved.version } : null)); }}><NativeSelectOption value="">No section</NativeSelectOption>{sections.map(s => <NativeSelectOption key={s.id} value={s.id}>{s.title}</NativeSelectOption>)}</NativeSelect></Field>}
          </div>
          <TagPicker targetKind="Task" targetId={editing.id} label="Tags" tags={tags} api={tagApi} revision={revision} onChanged={changed}/>
          </TabsContent>

        <TabsContent value="subtasks">
        <section className="grid gap-3" aria-labelledby="subtasks-heading">
          <h3 id="subtasks-heading" className="flex items-center font-semibold">Subtasks{subtasks.length > 0 && <Count>{subtasks.filter(s => s.status === "Done").length}/{subtasks.length}</Count>}</h3>
          {subtasks.length > 0 && <ol className="grid gap-1" aria-label="Subtasks">{subtasks.map((subtask, index) => <li key={subtask.id} className={rowClass(subtaskEdit?.id === subtask.id, subtask.status, true)}
            {...drag.target(subtasks.map(row => row.id), index, `subtasks|${editing.id}`, busy, (id, insertionIndex) => reorderSubtask(subtasks.find(row => row.id === id)!, insertionIndex))}>
            {drag.handle(subtask.id, `subtasks|${editing.id}`, busy)}
            <div className="min-w-0 flex-1"><button data-slot="row-title" className="w-fit rounded-sm px-1 py-0.5 text-left [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={busy} onClick={() => setSubtaskEdit(subtask)}>{subtask.title}</button></div>
            <StatusBadge status={subtask.status}/>
            {rowActions(subtask.title, index, subtasks.length, to => reorderSubtask(subtask, to), () => void runSubtask(() => subtaskRpc.delete({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, title: subtask.title, notes: subtask.notes }), () => { if (subtaskEdit?.id === subtask.id) setSubtaskEdit(null); }))}
          </li>)}</ol>}
          <form className="flex gap-2" onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.add({ operationId: operationId(), taskId: editing.id, title: subtaskTitle, notes: "" }), () => setSubtaskTitle("")); }}>
            <label htmlFor="new-subtask" className="sr-only">New subtask</label>
            <Input id="new-subtask" className="border-dashed bg-transparent focus-visible:border-solid" value={subtaskTitle} onChange={e => setSubtaskTitle(e.target.value)} placeholder="Add a subtask" autoComplete="off"/>
            <Button variant="outline" disabled={busy || !subtaskTitle.trim()}>Add subtask</Button>
          </form>
          {subtaskEdit && <Card className="gap-4 py-4"><CardContent className="px-4"><form className="grid gap-4" onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.edit({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, title: subtaskEdit.title, notes: subtaskEdit.notes }), () => setSubtaskEdit(null)); }}>
            <h4 className="font-semibold">Edit subtask</h4>
            <Field label="Title"><Input required value={subtaskEdit.title} onChange={e => setSubtaskEdit({ ...subtaskEdit, title: e.target.value })}/></Field>
            <MarkdownEditor id="subtask-notes" label="Subtask notes" rows={3} value={subtaskEdit.notes} onChange={notes => setSubtaskEdit({ ...subtaskEdit, notes })} links={links} searchLinks={searchLinks}/>
            <Field label="Status"><NativeSelect value={subtaskEdit.status} disabled={busy} onChange={e => { const status = e.target.value; void runSubtask(() => subtaskRpc.setStatus({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, status }), saved => setSubtaskEdit(current => current ? { ...current, status: saved.status, version: saved.version } : null)); }}>{statuses.map(s => <NativeSelectOption key={s} value={s}>{label(s)}</NativeSelectOption>)}</NativeSelect></Field>
            <div className="flex gap-2"><Button disabled={busy}>Save subtask</Button><Button type="button" variant="outline" onClick={() => setSubtaskEdit(null)}>Cancel</Button></div>
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
        <TabsContent value="recurrence"><RecurrenceEditor task={editing} revision={revision} busy={busy} onError={setError} taskApi={taskRpc} api={recurrenceApi}
          onChanged={saved => { setEditing(current => current?.id === saved.id ? { ...current, status: saved.status, version: saved.version, plannedDate: saved.plannedDate, plannedTime: saved.plannedTime, plannedZoneId: saved.plannedZoneId, dueDate: saved.dueDate, dueTime: saved.dueTime, dueZoneId: saved.dueZoneId } : current); void refresh().catch((e: unknown) => setError(String(e))); changed(); }}/></TabsContent>
        </div>
      </Tabs>
      <div className="flex gap-2 border-t px-5 py-3">
        <Button form="task-editor-form" disabled={busy}>Save</Button>
        <Button type="button" variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
      </div>
    </aside>}

    {recurrenceConflict && <div className="fixed inset-0 z-40 grid place-items-center bg-black/40 p-4"><div role="dialog" aria-modal="true" aria-labelledby="recurrence-conflict-title" className="grid max-w-md gap-3 rounded-xl border bg-card p-5 shadow-lg"><h2 id="recurrence-conflict-title" className="font-semibold">Review the later occurrence</h2><p className="text-sm">{recurrenceConflict.message}</p><p className="text-sm">The completion and the later work are unchanged. Open the later task to review its edits and diary before deciding how to reconcile them.</p><div className="flex gap-2"><Button type="button" onClick={() => { const id = recurrenceConflict.taskId; setRecurrenceConflict(null); void taskRpc.get({ taskId: id }).then(result => { if (result.ok && result.task) setEditing(result.task); else setError(result.error?.message ?? "Could not open the task."); }).catch((error: unknown) => setError(String(error))); }}>Open later task</Button><Button type="button" variant="outline" onClick={() => setRecurrenceConflict(null)}>Keep current state</Button></div></div></div>}
    {recurrenceDelete && <div className="fixed inset-0 z-40 grid place-items-center bg-black/40 p-4"><div role="dialog" aria-modal="true" aria-labelledby="recurrence-delete-title" className="grid max-w-md gap-3 rounded-xl border bg-card p-5 shadow-lg"><h2 id="recurrence-delete-title" className="font-semibold">Delete repeating task?</h2><p className="text-sm">Skip this occurrence and continue the series, or end the series and delete this task. Earlier history stays available.</p><div className="flex flex-wrap gap-2"><Button type="button" disabled={busy} onClick={() => { const task = recurrenceDelete; setRecurrenceDelete(null); void run(() => recurrenceApi.skip({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); }); }}>Skip this occurrence</Button><Button type="button" variant="outline" disabled={busy} onClick={() => { const task = recurrenceDelete; setRecurrenceDelete(null); void run(() => recurrenceApi.endAndDelete({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); }); }}>End and delete</Button><Button type="button" variant="ghost" onClick={() => setRecurrenceDelete(null)}>Cancel</Button></div></div></div>}
    <AlertStack api={reminderApi} onOpen={id => void openTaskById(id)} onChanged={changed}/>
    {lastUndo && <div role="status" aria-label="Undo" className="fixed bottom-4 left-1/2 z-30 flex max-w-[min(32rem,calc(100vw-2rem))] -translate-x-1/2 items-center gap-3 rounded-xl border bg-popover py-1.5 pr-1.5 pl-4 text-sm shadow-lg">
      <span className="truncate">{lastUndo.label}</span>
      <Button type="button" size="sm" variant="outline" disabled={busy} title="Undo (Ctrl+Z outside text fields)" onClick={() => void undoLatest()}><Undo2/>Undo</Button>
      <Button type="button" variant="ghost" size="icon-sm" aria-label="Dismiss undo" onClick={() => setLastUndo(null)}><X/></Button>
    </div>}
  </div>;
}
