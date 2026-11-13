import { TaskViewContext } from "./TaskViewAction";
import { TaskEditor } from "./TaskEditor";
import { ListPage } from "./ListPage";
import React from "react";
import { taskRpc, recurrenceRpc, subtaskRpc, containerRpc, tagRpc, queryRpc, undoRpc, markdownRpc, diaryRpc, historyRpc, padRpc, captureRpc, myDayRpc, myWeekRpc, reminderRpc, waitingRpc, journalRpc, shellRpc, type ShellActivationDto, type CaptureDraftDto, type PadItemResponse, type TaskResponse, type TaskOperationResponse, type ContainerResponse, type SectionResponse, type TagResponse, type UndoEntryResponse } from "#neoastra";
import { type LinkCandidate } from "./MarkdownEditor";
import { type MarkdownLinks } from "./markdown";
import { Templates } from "./Templates";
import { createLatestRequest } from "./latestRequest";
import { useDragReorder } from "./dragReorder";
import { QueryView, builtInViews, type QueryState } from "./QueryView";
import { NewTagForm, TagSettings, TagTree } from "./tags";
import { ScratchPad, padExcerpt, type PadFocus } from "./ScratchPad";
import { DraftRegistry } from "./padAutosave";
import { MyDay, useDayClock } from "./MyDay";
import { backgroundImage, useDayBackground } from "./MyDayBackground";
import { MyWeek } from "./MyWeek";
import { AlertStack, NotificationCenter } from "./Reminders";
import { Journal, SummaryPrompts } from "./Journal";
import { DataSettings } from "./DataSettings";
import { Settings } from "./Settings";
import { useTimeZones } from "./ZoneSelect";
import { CategoryDot, FocusSwitch, asCategory, useFocus, type Category } from "./category";
import { AlarmClock, ChevronRight, Plus, Bell, Cog, Database, BookOpen, Sun, CalendarCheck, CalendarDays, CalendarRange, CircleCheckBig, FolderKanban, Hourglass, Inbox, LayoutTemplate, ListTodo, LogOut, Search, Settings2, StickyNote, Undo2, UserRound, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, Count, DeletedItems, PageHeader, isFinished, label, priorities, statuses } from "@/components/common";

const viewIcons: Record<string, React.ReactNode> = { Today: <CalendarCheck/>, Upcoming: <CalendarRange/>, Overdue: <AlarmClock/>, Waiting: <Hourglass/>, RecentlyCompleted: <CircleCheckBig/>, AssignedToMe: <UserRound/> };

type Api = { taskRpc: typeof taskRpc; containerRpc: typeof containerRpc; recurrenceRpc?: typeof recurrenceRpc; tagRpc?: typeof tagRpc; queryRpc?: typeof queryRpc; undoRpc?: typeof undoRpc; markdownRpc?: typeof markdownRpc; diaryRpc?: typeof diaryRpc; historyRpc?: typeof historyRpc; padRpc?: typeof padRpc; captureRpc?: typeof captureRpc; myDayRpc?: typeof myDayRpc; myWeekRpc?: typeof myWeekRpc; reminderRpc?: typeof reminderRpc; waitingRpc?: typeof waitingRpc; journalRpc?: typeof journalRpc; shellRpc?: typeof shellRpc };
type View = "tasks" | "templates" | "query" | "pad" | "myday" | "myweek" | "notifications" | "journal" | "data" | "settings";

/** Focus is in a text field, where Ctrl+Z belongs to the field's own text undo (PLAN §9). */
function isTextEditing(target: EventTarget | null) {
  const element = target as HTMLElement | null;
  return !!element && (element.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(element.tagName));
}

/** The main window. A fresh launch opens My Day (PLAN §13); tests may start elsewhere. */
export function App({ api = { taskRpc, containerRpc }, templates, initialView = "myday" }: { api?: Api; templates?: React.ReactNode; initialView?: View }) {
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
  const shellApi = api.shellRpc ?? shellRpc;
  const dayBackground = useDayBackground();
  const dayClock = useDayClock(myDayApi);
  const timeZones = useTimeZones(shellApi, dayClock?.zoneId);
  const quickAddInput = React.useRef<HTMLTextAreaElement>(null);
  const drafts = React.useRef(new DraftRegistry()).current;
  const [padFocus, setPadFocus] = React.useState<PadFocus | null>(null);
  const [padMatches, setPadMatches] = React.useState<PadItemResponse[]>([]);
  const [lastUndo, setLastUndo] = React.useState<UndoEntryResponse | null>(null);
  React.useEffect(() => {
    if (!lastUndo) return;
    const timer = window.setTimeout(() => setLastUndo(null), 5000);
    return () => window.clearTimeout(timer);
  }, [lastUndo]);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [tasks, setTasks] = React.useState<TaskResponse[]>([]);
  const [editing, setEditing] = React.useState<TaskResponse | null>(null);
  const [viewingTask, setViewingTask] = React.useState(false);
  React.useEffect(() => { if (!editing) setViewingTask(false); }, [editing]);
  const [recurrenceDelete, setRecurrenceDelete] = React.useState<TaskResponse | null>(null);
  const [recurrenceConflict, setRecurrenceConflict] = React.useState<{ taskId: string; message: string } | null>(null);
  const [deleted, setDeleted] = React.useState<TaskResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [warning, setWarning] = React.useState<string | null>(null);
  React.useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(null), 5000);
    return () => window.clearTimeout(timer);
  }, [notice]);
  const [busy, setBusy] = React.useState(false);
  const [containers, setContainers] = React.useState<ContainerResponse[]>([]);
  const [deletedContainers, setDeletedContainers] = React.useState<ContainerResponse[]>([]);
  const [containerId, setContainerId] = React.useState("");
  const [containerName, setContainerName] = React.useState("");
  const [containerKind, setContainerKind] = React.useState("List");
  const [addingContainer, setAddingContainer] = React.useState(false);
  const [addingTag, setAddingTag] = React.useState(false);
  const [sections, setSections] = React.useState<SectionResponse[]>([]);
  const [subtaskRevision, setSubtaskRevision] = React.useState(0);
  const [view, setView] = React.useState<View>(initialView);
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
  const [focus, setFocus] = useFocus();
  const focusRef = React.useRef(focus);
  focusRef.current = focus;
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
    const [list, removed, counts] = await Promise.all([tagApi.list({}), tagApi.listDeleted({}), queryApi.counts({ category: focusRef.current })]);
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
  /**
   * The category a captured task takes: an explicit choice wins, then the destination list's
   * default (applied by the server), then the current focus; otherwise the server's Personal.
   */
  function withCategory(draft: CaptureDraftDto): CaptureDraftDto {
    if (draft.category) return draft;
    const destination = containers.find(c => c.id === (draft.containerId ?? containerId)) ?? (draft.containerId ? undefined : containers.find(c => c.kind === "Inbox"));
    return destination?.defaultCategory || !focus ? draft : { ...draft, category: focus };
  }
  async function saveCaptured(captured: CaptureDraftDto[], operation: string): Promise<string | null> {
    const result = await captureApi.create({ operationId: operation, drafts: captured.map(withCategory) });
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
      if (result.ok && result.task) { setViewingTask(false); setEditing(result.task); } else setError(result.error?.message ?? "Could not open the task.");
    } catch (e) { setError(String(e)); }
  }

  /** What the desktop shell asked for: a global shortcut, the tray, another launch or a notification button (PLAN §13). */
  function applyActivations(items: readonly ShellActivationDto[]) {
    for (const item of items) {
      if (item.kind === "Capture") focusQuickAdd();
      else if (item.kind === "Pad") openPad({ itemId: null });
      else if (item.kind === "OpenTask" && item.taskId) { setShowTagSettings(false); void openTaskById(item.taskId); }
      if (item.message) setWarning(item.message);
      if (item.kind === "Notice") changed();
    }
  }
  const activationsRef = React.useRef(applyActivations);
  activationsRef.current = applyActivations;
  // The shell queues activations; take them when shown, when focused and once a second.
  // Outside the desktop host (no shell service) taking fails and polling stops.
  React.useEffect(() => {
    let stopped = false;
    let taking = false;
    async function take() {
      if (stopped || taking) return;
      taking = true;
      try {
        const result = await shellApi.take({});
        if (!stopped && result.ok && result.activations.length > 0) activationsRef.current(result.activations);
      } catch { stop(); }
      finally { taking = false; }
    }
    const timer = window.setInterval(() => void take(), 1000);
    const wake = () => void take();
    function stop() {
      stopped = true;
      window.clearInterval(timer);
      window.removeEventListener("focus", wake);
      document.removeEventListener("visibilitychange", wake);
    }
    window.addEventListener("focus", wake);
    document.addEventListener("visibilitychange", wake);
    void take();
    return stop;
  }, [shellApi]);

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
      queryApi.run({ view: "Search", text, containerId: null, tagId: null, statuses: null, priorities: null, plannedFrom: null, plannedTo: null, dueFrom: null, dueTo: null, completedFrom: null, completedTo: null, includeCompleted: true, includeArchived: false, upcomingDays: null, limit: 8, cursor: null, day: null, category: null }),
      padApi.search({ text, limit: 4 }).catch(() => null),
    ]);
    const tasks: LinkCandidate[] = result.ok ? result.rows.map(row => ({ id: row.task.id, title: row.task.title })) : [];
    const pads: LinkCandidate[] = notes?.ok ? notes.items.map(item => ({ id: item.id, title: padExcerpt(item.markdown), kind: "pad" as const })) : [];
    return [...tasks.slice(0, 6), ...pads];
  }, [queryApi, padApi]);
  React.useEffect(() => { void refreshContainers().catch((e: unknown) => setError(String(e))); }, []);
  React.useEffect(() => { void refreshMeta().catch((e: unknown) => setError(String(e))); }, [focus]);
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
        await refreshContainers(); setView("tasks"); selectContainer(result.container.id); setContainerName(""); setAddingContainer(false);
      } catch (err) { setError(String(err)); } finally { setBusy(false); }
    })();
  }
  /** Switches a task between Work and Personal, keeping every other field as saved. */
  function setTaskCategory(task: TaskResponse, category: Category) {
    if (task.category === category) return;
    void run(() => taskRpc.edit({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, title: task.title, notes: task.notes,
      plannedDate: task.plannedDate || null, plannedTime: task.plannedTime || null, plannedZoneId: task.plannedZoneId || null,
      dueDate: task.dueDate || null, dueTime: task.dueTime || null, dueZoneId: task.dueZoneId || null,
      priority: task.priority, estimateMinutes: task.estimateMinutes, category }),
      saved => setEditing(current => current?.id === saved.id ? { ...current, category: saved.category, version: saved.version } : current));
  }
  function setTaskStatus(task: TaskResponse, status: string) {
    void run(async () => {
      const open = status === "Done" && (await refreshSubtasks(task.id)).some(subtask => !isFinished(subtask.status));
      const choice = open ? (window.confirm("Complete open subtasks too? Choose Cancel to keep them open.") ? "CompleteAll" : "KeepOpen") : null;
      return taskRpc.setStatus({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, status, subtaskChoice: choice });
    }, saved => { setEditing(current => current?.id === saved.id ? { ...current, status: saved.status, version: saved.version } : current); if (editing?.id === saved.id) setSubtaskRevision(value => value + 1); });
  }
  const selected = containers.find(c => c.id === containerId);
  const listName = selected?.name ?? "Inbox";
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

  function viewTask(task: TaskResponse | string) {
    if (typeof task !== "string") { setViewingTask(true); setEditing(task); return; }
    void taskRpc.get({ taskId: task }).then(result => {
      if (result.ok && result.task) { setViewingTask(true); setEditing(result.task); }
      else setError(result.error?.message ?? "Could not open the task.");
    }).catch((error: unknown) => setError(String(error)));
  }

  return <TaskViewContext.Provider value={viewTask}><div className={cn("grid min-h-screen grid-cols-[15.5rem_minmax(0,1fr)] overflow-x-clip pt-[40px]", showDetail && "grid-cols-[15.5rem_minmax(0,1fr)_minmax(21rem,26rem)]")} aria-busy={busy}>
    <div className="window-titlebar fixed inset-x-0 top-0 z-40 flex h-[40px] items-center bg-[#1c1917] px-4 text-sm text-white select-none" aria-hidden="true">Chaos</div>
    <a href="#main-content" className="sr-only rounded-md bg-primary px-3 py-2 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50">Skip to content</a>
    <aside className="sticky top-[40px] flex h-[calc(100vh-40px)] flex-col gap-1 overflow-y-auto border-r border-sidebar-border bg-sidebar px-3 py-4 text-sidebar-foreground">
      <h1 className="flex items-center gap-2 px-2 pb-3 text-lg font-bold tracking-tight"><img src={new URL("../../../../eng/assets/chaos-icon.svg", import.meta.url).href} alt="" className="size-5"/>Chaos</h1>
      <form role="search" className="mb-2 flex items-center gap-1.5 rounded-md border border-input bg-background px-2 focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/30" onSubmit={search}>
        <Search className="size-4 shrink-0 text-muted-foreground"/>
        <label htmlFor="search" className="sr-only">Search tasks</label>
        <input id="search" type="search" dir="auto" className="h-8 min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground" value={searchText} onChange={e => setSearchText(e.target.value)} placeholder="Search tasks" autoComplete="off"/>
      </form>
      <FocusSwitch focus={focus} onChange={setFocus}/>
      <nav aria-label="Views" className="grid gap-px">
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "myday" ? "page" : undefined} onClick={() => { setEditing(null); setShowTagSettings(false); setView("myday"); }}>
          <Sun/>My Day{(viewCounts.get("MyDay") ?? 0) > 0 && <span className="ml-auto text-xs text-muted-foreground tabular-nums">{viewCounts.get("MyDay")}</span>}
        </Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "myweek" ? "page" : undefined} onClick={() => { setEditing(null); setShowTagSettings(false); setView("myweek"); }}><CalendarDays/>My Week</Button>
        <Button type="button" variant="ghost" className={navItem} aria-current={view === "tasks" && !containerId ? "page" : undefined} onClick={() => openContainer("")}><Inbox/>Inbox</Button>
        <SidebarSection id="views" title="Views">
          {builtInViews.map(item => <Button key={item.view} type="button" variant="ghost" className={navItem} aria-current={view === "query" && query.view === item.view ? "page" : undefined} onClick={() => openQuery({ view: item.view })}>
            {viewIcons[item.view]}{item.title}{(viewCounts.get(item.view) ?? 0) > 0 && <span className="ml-auto text-xs text-muted-foreground tabular-nums">{viewCounts.get(item.view)}</span>}
          </Button>)}
        </SidebarSection>
        <SidebarSection id="notes" title="Notes">
          <Button type="button" variant="ghost" className={navItem} aria-current={view === "pad" ? "page" : undefined} title="Personal pad (Ctrl+Shift+P for a new note)" onClick={() => { setShowTagSettings(false); setView("pad"); }}><StickyNote/>Personal pad</Button>
          <Button type="button" variant="ghost" className={navItem} aria-current={view === "journal" ? "page" : undefined} onClick={() => { setShowTagSettings(false); setView("journal"); }}><BookOpen/>Journal</Button>
        </SidebarSection>
      </nav>
      <SidebarSection id="lists" title="Lists & projects" addLabel="New list or project" adding={addingContainer || userContainers.length === 0} onAdd={() => setAddingContainer(v => !v)}
        form={<form className="mt-1 grid gap-2 rounded-lg border border-dashed border-input p-2" onSubmit={createContainer}>
          <label className="sr-only" htmlFor="new-container">New list or project</label>
          <Input id="new-container" autoFocus={addingContainer} placeholder="New list or project" className="bg-background" value={containerName} onChange={e => setContainerName(e.target.value)} onKeyDown={e => { if (e.key === "Escape") setAddingContainer(false); }} autoComplete="off"/>
          <div className="flex gap-2">
            <label className="sr-only" htmlFor="new-container-kind">Type</label>
            <div className="flex-1 [&_[data-slot=native-select-wrapper]]:w-full"><NativeSelect id="new-container-kind" className="bg-background" value={containerKind} onChange={e => setContainerKind(e.target.value)}><NativeSelectOption>List</NativeSelectOption><NativeSelectOption>Project</NativeSelectOption></NativeSelect></div>
            <Button disabled={busy || !containerName.trim()}>Create</Button>
          </div>
        </form>}>
        {userContainers.length > 0
          ? <nav aria-label="Lists and projects"><ol className="grid gap-px">{containers.map((c, index) => c.kind === "Inbox" ? null : <li key={c.id} className="group/row flex items-center rounded-md"
              {...drag.target(containers.map(row => row.id), index, "containers", busy, (id, insertionIndex) => { const moving = containers.find(row => row.id === id)!; void runContainer(() => containerRpc.reorder({ operationId: operationId(), containerId: moving.id, expectedVersion: moving.version, insertionIndex }), "Could not reorder lists."); })}>
              {drag.handle(c.id, "containers", busy, "w-0 overflow-hidden p-0 group-focus-within/row:w-auto group-focus-within/row:px-0.5 group-hover/row:w-auto group-hover/row:px-0.5")}
              <Button type="button" variant="ghost" className={cn(navItem, c.archived && "text-muted-foreground", focus && asCategory(c.defaultCategory) && asCategory(c.defaultCategory) !== focus && "opacity-50 hover:opacity-100 aria-[current=page]:opacity-100")} disabled={busy} aria-current={view === "tasks" && c.id === containerId ? "page" : undefined} onClick={() => openContainer(c.id)}>{c.kind === "Project" ? <FolderKanban/> : <ListTodo/>}<span className="truncate">{c.name}</span>{asCategory(c.defaultCategory) && <CategoryDot category={asCategory(c.defaultCategory)!} className="mr-1 ml-auto"/>}</Button>
              {c.archived && <span className="pr-2 text-[0.7rem] text-muted-foreground">archived</span>}
            </li>)}</ol></nav>
          : <p className="px-2 py-1 text-muted-foreground">No lists yet. Create one below.</p>}
      </SidebarSection>
      <SidebarSection id="tags" title="Tags" addLabel="New tag" adding={addingTag} onAdd={() => setAddingTag(v => !v)}
        form={<NewTagForm busy={busy} autoFocus onCreate={async path => { const created = await createTag(path); if (created) setAddingTag(false); return created; }}/>}>
        <TagTree tags={tags} counts={tagCounts} activeTagId={activeTag?.id ?? null} busy={busy} onOpen={tag => openQuery({ view: "Tag", tagId: tag.id })} onTagTask={(taskId, tag) => void tagTask(taskId, tag)}/>
        <DeletedItems className="pt-2" noun="tags" items={deletedTags} name={tag => tag.name} busy={busy} onRestore={tag => void restoreTag(tag)}/>
      </SidebarSection>
      <DeletedItems className="pt-4" noun="lists" items={deletedContainers} name={item => item.name} busy={busy} onRestore={item => void runContainer(() => containerRpc.restore({ operationId: operationId(), containerId: item.id, expectedVersion: item.version, name: item.name }), "Could not restore list.")}/>
      <nav aria-label="App" className="sticky -bottom-4 mt-auto -mx-3 -mb-4 flex items-center justify-between gap-1 border-t border-sidebar-border bg-sidebar px-3 py-2">
        {([
          ["notifications", "Notifications", <Bell/>],
          ["templates", "Templates", <LayoutTemplate/>],
          ["data", "Data & backups", <Database/>],
          ["settings", "Settings", <Cog/>],
        ] as const).map(([target, name, icon]) => <Button key={target} type="button" variant="ghost" size="icon-sm" aria-label={name} title={name}
          className="text-muted-foreground hover:bg-sidebar-accent aria-[current=page]:bg-brand-soft aria-[current=page]:text-primary"
          aria-current={view === target ? "page" : undefined}
          onClick={() => { if (target !== "notifications") setEditing(null); setShowTagSettings(false); setView(target); }}>{icon}</Button>)}
        <Button type="button" variant="ghost" size="icon-sm" aria-label="Quit Chaos" title="Quit Chaos" className="text-muted-foreground hover:bg-sidebar-accent" onClick={() => void shellApi.quit({})}><LogOut/></Button>
      </nav>
    </aside>

    <div className="min-w-0" style={view === "myday" && dayBackground.background.kind !== "default" ? {
      backgroundColor: dayBackground.background.kind === "color" ? dayBackground.background.value : undefined,
      backgroundImage: backgroundImage(dayBackground.background) ? `url("${backgroundImage(dayBackground.background)}")` : undefined,
      backgroundSize: "cover", backgroundPosition: "center", backgroundAttachment: "fixed",
    } : undefined}>
    <div id="main-content" tabIndex={-1} className={cn("mx-auto grid w-full max-w-4xl min-w-0 content-start gap-5 px-[clamp(1rem,4vw,3rem)] pt-8 pb-16 outline-none", view === "myday" && dayBackground.background.kind !== "default" && "min-h-[calc(100vh-40px)] bg-background/90")}>
      {error && <Banner tone="error" onDismiss={() => setError(null)}>{error}</Banner>}
      {warning && <Banner tone="warning" onDismiss={() => setWarning(null)}>{warning}</Banner>}
      {notice && <Banner tone="warning" onDismiss={() => setNotice(null)}>{notice}</Banner>}
      <SummaryPrompts api={summaryApi} onOpen={id => { setJournalOpenId(id); setShowTagSettings(false); setView("journal"); }}/>

      {view === "pad" ? <ScratchPad api={padApi} tagApi={tagApi} captureApi={captureApi} registry={drafts} containers={containers} tags={tags} revision={revision} focus={padFocus}
          links={links} searchLinks={searchLinks} onChanged={changed} onOpenTask={taskId => void openLinkedTask(taskId)} onError={setError}/>
      : view === "myweek" ? <MyWeek clock={dayClock} revision={revision} busy={busy} containers={containers} api={myWeekApi} onOpen={task => { setViewingTask(false); setEditing(task); }} onChanged={changed} focus={focus}/>
      : view === "myday" ? <MyDay clock={dayClock} revision={revision} selectedTaskId={editing?.id ?? null} busy={busy} api={myDayApi} queryApi={queryApi} waitingApi={waitingApi} onOpen={task => { setViewingTask(false); setEditing(task); }} onView={task => { setViewingTask(true); setEditing(task); }} onChanged={changed} onSetStatus={setTaskStatus} background={dayBackground.background} onBackgroundChange={dayBackground.save} focus={focus} onSetCategory={setTaskCategory}/>
      : view === "journal" ? <Journal api={summaryApi} revision={revision} today={dayClock?.today ?? ""} openId={journalOpenId} onOpenTask={id => void openTaskById(id)} onChanged={changed}/>
      : view === "data" ? <DataSettings containers={containers} onImported={() => { void refreshMeta().catch((e: unknown) => setError(String(e))); changed(); }} onReplaced={() => { setEditing(null); void refreshMeta().catch((e: unknown) => setError(String(e))); changed(); }}/>
      : view === "settings" ? <Settings api={shellApi} onSaved={() => changed()} onGitHubSynced={() => { changed(); void refreshContainers().catch((e: unknown) => setError(String(e))); }} onNavigate={next => { setEditing(null); setView(next); }}/>
      : view === "notifications" ? <NotificationCenter revision={revision} api={reminderApi} onOpen={id => void openTaskById(id)}/>
      : view === "query" ? <QueryView query={query} tags={tags} revision={revision} selectedTaskId={editing?.id ?? null} busy={busy} api={queryApi} recurrenceApi={recurrenceApi} onOpen={task => { setViewingTask(false); setEditing(task); }} header={queryHeader} focus={focus} onSetCategory={setTaskCategory}/>
      : view === "templates" ? (templates ?? <Templates timeZones={timeZones} containerId={containerId} containerName={listName} onCreated={refresh}/>) : <>
        <ListPage tasks={tasks} deleted={deleted} sections={sections} setSections={setSections} containers={containers} containerId={containerId} selected={selected}
          busy={busy} setBusy={setBusy} setError={setError} run={run} runContainer={runContainer} refresh={refresh} refreshContainers={refreshContainers}
          onDeleted={async () => { if (selectedContainer.current === containerId) selectContainer(""); await refreshContainers(); }}
          isStillSelected={id => selectedContainer.current === id}
          editingId={editing?.id ?? null} setEditing={setEditing} onOpen={(task, viewing) => { setViewingTask(viewing); setEditing(task); }}
          onDelete={task => void requestTaskDeletion(task)} onSetStatus={setTaskStatus} onSetCategory={setTaskCategory}
          revision={revision} subtaskRevision={subtaskRevision} focus={focus} setFocus={setFocus}
          showSettings={showSettings} setShowSettings={setShowSettings} captureApi={captureApi} onCapture={saveCaptured} quickAddInput={quickAddInput} containerApi={containerRpc} taskApi={taskRpc}/>
      </>}
    </div>
    </div>


    {editing && view !== "templates" && <TaskEditor editing={editing} setEditing={setEditing} viewingTask={viewingTask} setViewingTask={setViewingTask}
      busy={busy} setBusy={setBusy} setError={setError} setNotice={setNotice} setWarning={setWarning} run={run} changed={changed} refresh={refresh}
      revision={revision} subtaskRevision={subtaskRevision} setSubtaskRevision={setSubtaskRevision}
      canAddToMyDay={!!dayClock} onAddToMyDay={task => void addToMyDay(task)} links={links} searchLinks={searchLinks} timeZones={timeZones}
      containers={containers} containerId={containerId} sections={sections} tags={tags} openPad={openPad}
      api={{ taskRpc, containerRpc, tagApi, markdownApi, padApi, journalApi, reminderApi, waitingApi, recurrenceApi }}/>}

    {recurrenceConflict && <div className="fixed inset-0 z-40 grid place-items-center bg-black/40 p-4"><div role="dialog" aria-modal="true" aria-labelledby="recurrence-conflict-title" className="grid max-w-md gap-3 rounded-xl border bg-card p-5 shadow-lg"><h2 id="recurrence-conflict-title" className="font-semibold">Review the later occurrence</h2><p className="text-sm">{recurrenceConflict.message}</p><p className="text-sm">The completion and the later work are unchanged. Open the later task to review its edits and diary before deciding how to reconcile them.</p><div className="flex gap-2"><Button type="button" onClick={() => { const id = recurrenceConflict.taskId; setRecurrenceConflict(null); void taskRpc.get({ taskId: id }).then(result => { if (result.ok && result.task) setEditing(result.task); else setError(result.error?.message ?? "Could not open the task."); }).catch((error: unknown) => setError(String(error))); }}>Open later task</Button><Button type="button" variant="outline" onClick={() => setRecurrenceConflict(null)}>Keep current state</Button></div></div></div>}
    {recurrenceDelete && <div className="fixed inset-0 z-40 grid place-items-center bg-black/40 p-4"><div role="dialog" aria-modal="true" aria-labelledby="recurrence-delete-title" className="grid max-w-md gap-3 rounded-xl border bg-card p-5 shadow-lg"><h2 id="recurrence-delete-title" className="font-semibold">Delete repeating task?</h2><p className="text-sm">Skip this occurrence and continue the series, or end the series and delete this task. Earlier history stays available.</p><div className="flex flex-wrap gap-2"><Button type="button" disabled={busy} onClick={() => { const task = recurrenceDelete; setRecurrenceDelete(null); void run(() => recurrenceApi.skip({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); }); }}>Skip this occurrence</Button><Button type="button" variant="outline" disabled={busy} onClick={() => { const task = recurrenceDelete; setRecurrenceDelete(null); void run(() => recurrenceApi.endAndDelete({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); }); }}>End and delete</Button><Button type="button" variant="ghost" onClick={() => setRecurrenceDelete(null)}>Cancel</Button></div></div></div>}
    <AlertStack api={reminderApi} onOpen={id => void openTaskById(id)} onChanged={changed}/>
    {lastUndo && <div role="status" aria-label="Undo" className="fixed bottom-4 left-1/2 z-30 flex max-w-[min(32rem,calc(100vw-2rem))] -translate-x-1/2 items-center gap-3 rounded-xl border bg-popover py-1.5 pr-1.5 pl-4 text-sm shadow-lg">
      <span className="truncate">{lastUndo.label}</span>
      <Button type="button" size="sm" variant="outline" disabled={busy} title="Undo (Ctrl+Z outside text fields)" onClick={() => void undoLatest()}><Undo2/>Undo</Button>
      <Button type="button" variant="ghost" size="icon-sm" aria-label="Dismiss undo" onClick={() => setLastUndo(null)}><X/></Button>
    </div>}
  </div></TaskViewContext.Provider>;
}

/**
 * A titled sidebar group that folds away (remembered per viewer) with an optional add button
 * revealing a create form, so the sidebar shows only what is in use.
 */
function SidebarSection({ id, title, children, addLabel, adding = false, onAdd, form }: {
  id: string; title: string; children: React.ReactNode; addLabel?: string; adding?: boolean; onAdd?: () => void; form?: React.ReactNode;
}) {
  const key = `chaos.sidebar.${id}.collapsed`;
  const [collapsed, setCollapsed] = React.useState(() => { try { return localStorage.getItem(key) === "1"; } catch { return false; } });
  function toggle() {
    setCollapsed(current => { const next = !current; try { localStorage.setItem(key, next ? "1" : "0"); } catch { /* storage unavailable */ } return next; });
  }
  const bodyId = `sidebar-section-${id}`;
  return <section aria-label={title} className="mt-3 grid gap-px">
    <div className="flex items-center">
      <button type="button" className="flex min-w-0 flex-1 items-center gap-1 rounded-md px-2 py-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase hover:text-sidebar-foreground"
        aria-expanded={!collapsed} aria-controls={bodyId} onClick={toggle}>
        <ChevronRight className={cn("size-3 shrink-0 transition-transform", !collapsed && "rotate-90")} aria-hidden="true"/>{title}
      </button>
      {onAdd && <Button type="button" variant="ghost" size="icon-xs" className="text-muted-foreground hover:bg-sidebar-accent" aria-label={addLabel} title={addLabel} aria-expanded={adding}
        onClick={() => { if (collapsed) toggle(); onAdd(); }}><Plus/></Button>}
    </div>
    <div id={bodyId} hidden={collapsed} className="grid gap-px">
      {children}
      {adding && form}
    </div>
  </section>;
}
