import React from "react";
import { DateText, friendlyDate, relativeDays, useToday } from "./dates";
import { ContextMenu } from "radix-ui";
import { type captureRpc as CaptureRpc, type containerRpc as ContainerRpc, type taskRpc as TaskRpc, type CaptureDraftDto, type ContainerResponse, type SectionResponse, type TaskOperationResponse, type TaskResponse } from "#neoastra";
import { Archive, ArchiveRestore, CalendarDays, ChevronRight, Clock, Eye, Settings2, Trash2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Progress } from "@/components/ui/progress";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Count, DeletedItems, EmptyState, Field, PageHeader, PriorityBadge, StatusBadge, isFinished, label, statuses } from "@/components/common";
import { ContainerManagement } from "./ContainerManagement";
import { QuickAdd } from "./QuickAdd";
import { GitHubTaskMark } from "./GitHubTaskMark";
import { useDragReorder } from "./dragReorder";
import { taskDragType } from "./tags";
import { rowClass } from "./taskRow";
import { CategoryEdge, CategoryMenuItems, asCategory, categoryIcon, categoryTone, type Category, type Focus } from "./category";

const operationId = () => crypto.randomUUID();

/**
 * One list, project or the Inbox (PLAN §13): header and settings, quick add, completion
 * filter, sections with drag and keyboard ordering, and deleted tasks. Tasks outside the
 * Work/Personal focus are hidden here and counted so nothing disappears silently.
 */
export function ListPage({ tasks, deleted, sections, setSections, containers, containerId, selected, busy, setBusy, setError, run, runContainer, refresh, refreshContainers,
  onDeleted, isStillSelected, editingId, setEditing, onOpen, onDelete, onSetStatus, onSetCategory, revision, subtaskRevision, focus, setFocus,
  showSettings, setShowSettings, captureApi, onCapture, quickAddInput, containerApi, taskApi }: {
  tasks: TaskResponse[]; deleted: TaskResponse[]; sections: SectionResponse[]; setSections: (sections: SectionResponse[]) => void;
  containers: ContainerResponse[]; containerId: string; selected: ContainerResponse | undefined;
  busy: boolean; setBusy: (busy: boolean) => void; setError: (error: string | null) => void;
  run: (action: () => Promise<TaskOperationResponse>, after?: (task: TaskResponse) => void) => Promise<void>;
  runContainer: (action: () => Promise<{ ok: boolean; error: { message: string } | null }>, failure: string) => Promise<void>;
  refresh: () => Promise<void>; refreshContainers: () => Promise<void>; onDeleted: () => Promise<void>; isStillSelected: (containerId: string) => boolean;
  editingId: string | null; setEditing: React.Dispatch<React.SetStateAction<TaskResponse | null>>; onOpen: (task: TaskResponse, viewing: boolean) => void;
  onDelete: (task: TaskResponse) => void; onSetStatus: (task: TaskResponse, status: string) => void; onSetCategory: (task: TaskResponse, category: Category) => void;
  revision: number; subtaskRevision: number; focus: Focus; setFocus: (focus: Focus) => void;
  showSettings: boolean; setShowSettings: React.Dispatch<React.SetStateAction<boolean>>;
  captureApi: typeof CaptureRpc; onCapture: (drafts: CaptureDraftDto[], operation: string) => Promise<string | null>; quickAddInput: React.RefObject<HTMLTextAreaElement | null>;
  containerApi: typeof ContainerRpc; taskApi: typeof TaskRpc;
}) {
  const [completionFilter, setCompletionFilter] = React.useState("all");
  const [sectionDrop, setSectionDrop] = React.useState<string | null>(null);
  const [sectionTitle, setSectionTitle] = React.useState("");
  const drag = useDragReorder();
  const today = useToday();
  const [progress, setProgress] = React.useState<{ done: number; total: number } | null>(null);
  const isProject = selected?.kind === "Project";
  const taskVersions = tasks.map(task => `${task.id}:${task.version}`).join("|");
  React.useEffect(() => {
    let active = true;
    setProgress(null);
    if (isProject) void containerApi.progress({ containerId }).then(result => { if (active && result.ok) setProgress({ done: result.doneCount, total: result.totalCount }); }).catch(() => undefined);
    return () => { active = false; };
  }, [containerApi, containerId, isProject, taskVersions]);
  const listName = selected?.name ?? "Inbox";
  const openCount = tasks.filter(t => !isFinished(t.status)).length;
  const listDefault = asCategory(selected?.defaultCategory);
  const newTaskCategory: Category | null = listDefault ?? focus;
  const focusedTasks = focus ? tasks.filter(task => (asCategory(task.category) ?? "Personal") === focus) : tasks;
  const hiddenByFocus = tasks.length - focusedTasks.length;
  const visibleTasks = focusedTasks.filter(task => completionFilter === "all" || (completionFilter === "finished" ? isFinished(task.status) : !isFinished(task.status)));
  // "All" keeps finished tasks out of the sections, in one folded Finished group at the end.
  const sectionedTasks = completionFilter === "all" ? visibleTasks.filter(task => !isFinished(task.status)) : visibleTasks;
  const finishedTasks = completionFilter === "all" ? visibleTasks.filter(task => isFinished(task.status)) : [];
  const unsectioned = sectionedTasks.filter(task => task.sectionId === null);
  const knownSectionIds = new Set(sections.map(section => section.id));
  const unknownSections = [...new Set(sectionedTasks.map(task => task.sectionId).filter((id): id is string => id !== null && !knownSectionIds.has(id)))];
  function reorderTask(task: TaskResponse, insertionIndex: number) {
    void run(() => taskApi.reorder({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, insertionIndex }));
  }
  function sectionDropTarget(sectionId: string | null) {
    const key = sectionId ?? "";
    return {
      onDragOver: (event: React.DragEvent<HTMLElement>) => {
        if (busy || !containerId || !Array.from(event.dataTransfer.types ?? []).includes(taskDragType)) return;
        event.preventDefault(); event.dataTransfer.dropEffect = "move"; setSectionDrop(key);
      },
      onDragLeave: (event: React.DragEvent<HTMLElement>) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setSectionDrop(null);
      },
      onDragEnd: () => setSectionDrop(null),
      onDrop: (event: React.DragEvent<HTMLElement>) => {
        setSectionDrop(null);
        if (event.defaultPrevented || busy || !containerId || !Array.from(event.dataTransfer.types ?? []).includes(taskDragType)) return;
        const task = tasks.find(item => item.id === event.dataTransfer.getData(taskDragType));
        if (!task || task.containerId !== containerId || task.sectionId === sectionId) return;
        event.preventDefault();
        void run(() => containerApi.moveTask({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, containerId, sectionId }), saved => setEditing(current => current?.id === saved.id ? { ...current, sectionId: saved.sectionId, version: saved.version } : current));
      },
    };
  }
  function createSection(event: React.FormEvent) {
    event.preventDefault();
    void (async () => {
      setBusy(true);
      try {
        const result = await containerApi.createSection({ operationId: operationId(), containerId, title: sectionTitle });
        if (!result.ok) throw new Error(result.error?.message ?? "Could not create section.");
        const list = await containerApi.sections({ containerId });
        if (!list.ok) throw new Error(list.error?.message ?? "Could not load sections.");
        setSections([...list.sections]); setSectionTitle("");
      } catch (err) { setError(String(err)); } finally { setBusy(false); }
    })();
  }
  function taskMeta(task: TaskResponse) {
    const parts: React.ReactNode[] = [];
    if (task.status !== "NotStarted") parts.push(<StatusBadge key="status" status={task.status}/>);
    if (task.dueDate) parts.push(<DateText key="due" kind="due" date={task.dueDate} time={task.dueTime}><CalendarDays className="size-3.5"/></DateText>);
    else if (task.plannedDate) parts.push(<DateText key="planned" kind="planned" date={task.plannedDate} time={task.plannedTime}><CalendarDays className="size-3.5"/></DateText>);
    if (task.estimateMinutes) parts.push(<span key="estimate" className="inline-flex items-center gap-1"><Clock className="size-3.5"/>{task.estimateMinutes} min</span>);
    if (task.priority === "High" || task.priority === "Urgent") parts.push(<PriorityBadge key="priority" priority={task.priority}/>);
    return parts;
  }
  function taskList(rows: TaskResponse[], name: string) {
    const order = rows.map(row => row.id);
    const byId = (id: string) => rows.find(row => row.id === id)!;
    function move(task: TaskResponse, to: number) {
      const target = rows[to];
      if (!target) return;
      const siblings = tasks.filter(row => row.containerId === task.containerId && row.sectionId === task.sectionId);
      reorderTask(task, siblings.findIndex(row => row.id === target.id));
    }
    return <ol className="grid gap-1" aria-label={name}>{rows.map((task, index) => <li key={task.id} data-task-row={task.id}><ContextMenu.Root><ContextMenu.Trigger asChild><div className={rowClass(editingId === task.id, task.status)}
      onDragStart={event => event.dataTransfer?.setData?.(taskDragType, task.id)}
      {...drag.target(order, index, `${task.containerId}|${task.sectionId ?? ""}`, busy, (id, insertionIndex) => move(byId(id), insertionIndex))}>
      <CategoryEdge category={task.category}/>
      {drag.handle(task.id, `${task.containerId}|${task.sectionId ?? ""}`, busy)}
      <input type="checkbox" className="size-4 shrink-0 accent-primary" aria-label={`Mark ${task.title} as done`} checked={task.status === "Done"} disabled={busy} onChange={e => onSetStatus(task, e.target.checked ? "Done" : "NotStarted")}/>
      <div className="grid min-w-0 flex-1 gap-0.5">
        <div className="flex items-center gap-1">
          <button data-slot="row-title" className="w-fit min-w-0 rounded-sm px-1 py-0.5 text-left font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={busy} onClick={() => onOpen(task, false)}>{task.title}</button>
          <GitHubTaskMark taskId={task.id} version={task.version} revision={revision}/>
        </div>
        {taskMeta(task).length > 0 && <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-xs text-muted-foreground">{taskMeta(task)}</div>}
      </div>
      <div className="flex gap-0.5 opacity-0 transition-opacity group-focus-within/row:opacity-100 group-hover/row:opacity-100 [@media(hover:none)]:opacity-100">
        <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy} aria-label={`View ${task.title}`} title="View" onClick={() => onOpen(task, true)}><Eye/></Button>
        <Button variant="ghost" size="icon-sm" className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" disabled={busy} aria-label={`Delete ${task.title}`} title="Delete" onClick={() => onDelete(task)}><Trash2/></Button>
      </div>
    </div></ContextMenu.Trigger><ContextMenu.Portal><ContextMenu.Content aria-label={`Status for ${task.title}`} className="z-50 min-w-44 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-lg">
      <ContextMenu.Label className="px-2 py-1.5 text-xs text-muted-foreground">Set status</ContextMenu.Label>
      {(isFinished(task.status) ? ["NotStarted"] : statuses.filter(status => status !== "Done")).map(status => <ContextMenu.Item key={status} disabled={busy || status === task.status} className="cursor-default rounded-sm px-2 py-1.5 outline-none data-[highlighted]:bg-accent data-[disabled]:opacity-50" onSelect={() => void run(() => taskApi.setStatus({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, status, subtaskChoice: null }), saved => setEditing(current => current?.id === saved.id ? { ...current, status: saved.status, version: saved.version } : current))}>{isFinished(task.status) ? "Reopen (previous status)" : label(status)}</ContextMenu.Item>)}
      <CategoryMenuItems category={task.category} busy={busy} onSelect={category => onSetCategory(task, category)}/>
    </ContextMenu.Content></ContextMenu.Portal></ContextMenu.Root></li>)}</ol>;
  }
  function groupHeading(text: string, count?: number) {
    return <h3 className="flex items-center border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">{text}{count !== undefined && <Count>{count}</Count>}</h3>;
  }

  return <>
        <PageHeader eyebrow={`${selected ? selected.kind : "Inbox"}${selected?.archived ? " · archived" : ""}${selected?.deadlineDate ? ` · due ${friendlyDate(selected.deadlineDate, today)} (${relativeDays(selected.deadlineDate, today)})` : ""}`} title={listName}
          subtitle={tasks.length === 0 ? "Nothing here yet" : `${openCount} open · ${tasks.length - openCount} finished`}
          actions={selected && <>
            <Button type="button" variant="outline" disabled={busy} onClick={() => { const request = { operationId: operationId(), containerId: selected.id, expectedVersion: selected.version, name: selected.name }; void runContainer(() => selected.archived ? containerApi.unarchive(request) : containerApi.archive(request), "Could not update archive state."); }}>{selected.archived ? <ArchiveRestore/> : <Archive/>}{selected.archived ? "Unarchive" : "Archive"}</Button>
            <Button type="button" variant={showSettings ? "secondary" : "outline"} aria-expanded={showSettings} aria-controls="list-settings" onClick={() => setShowSettings(v => !v)}><Settings2/>{showSettings ? "Hide settings" : "Settings"}</Button>
          </>}/>

        {progress && progress.total > 0 && <div className="-mt-2 flex items-center gap-3 text-xs text-muted-foreground" aria-label="Project progress">
          <Progress value={Math.round(progress.done / progress.total * 100)} className="h-1.5 max-w-64" aria-label={`${progress.done} of ${progress.total} tasks done`}/>
          <span className="tabular-nums">{progress.done} of {progress.total} done</span>
        </div>}

        {selected && showSettings && <div id="list-settings" className="rounded-xl border bg-muted/50 p-4"><ContainerManagement container={selected} containers={containers} sections={sections} taskRevision={`${subtaskRevision}|${tasks.map(t => `${t.id}:${t.version}`).join("|")}`} onDeleted={onDeleted} onChanged={async () => { await refreshContainers(); const result = await containerApi.sections({ containerId }); if (!isStillSelected(containerId)) return; if (!result.ok) throw new Error(result.error?.message ?? "Could not load sections."); setSections([...result.sections]); await refresh(); }}/></div>}

        <QuickAdd key={containerId} api={captureApi} containers={containers} defaultContainerId={containerId} label="Add a task" inputRef={quickAddInput}
          placeholder="Add a task — try “Call Sam tomorrow 3pm #home ~personal”" onSubmit={onCapture}/>
        {newTaskCategory && <p className={cn("-mt-3 inline-flex items-center gap-1 px-1 text-xs [&_svg]:size-3.5", categoryTone[newTaskCategory].text)}>{categoryIcon[newTaskCategory]}New tasks here are {newTaskCategory}{listDefault ? " (list default)" : " (focus)"}</p>}

        <div className="flex flex-wrap items-center gap-3">
          <div role="radiogroup" aria-label="Completion" className="inline-flex rounded-md border bg-muted/40 p-0.5 text-xs">
            {([["all", "All"], ["open", "Open"], ["finished", "Finished"]] as const).map(([value, name]) => <button key={value} type="button" role="radio" aria-checked={completionFilter === value}
              className={cn("rounded-[5px] px-2.5 py-1 font-medium text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50", completionFilter === value && "bg-background text-foreground shadow-xs")}
              onClick={() => setCompletionFilter(value)}>{name}</button>)}
          </div>
          <p role="status" className="text-xs text-muted-foreground">Showing {visibleTasks.length} of {tasks.length} tasks{hiddenByFocus > 0 && ` · ${hiddenByFocus} ${focus === "Work" ? "personal" : "work"} hidden by focus`}</p>
          {hiddenByFocus > 0 && <Button type="button" variant="ghost" size="sm" onClick={() => setFocus(null)}>Show all</Button>}
        </div>
        {tasks.length === 0 && <EmptyState title="No tasks in this list.">Type above and press Enter to capture one. Ctrl+N jumps here from anywhere.</EmptyState>}
        {tasks.length > 0 && focusedTasks.length === 0 && <EmptyState title={`No ${focus?.toLowerCase()} tasks in this list.`}>Switch the focus to All to see its {hiddenByFocus} other {hiddenByFocus === 1 ? "task" : "tasks"}.</EmptyState>}
        {focusedTasks.length > 0 && visibleTasks.length === 0 && <EmptyState title={`No ${completionFilter} tasks.`}>Choose All to see everything in this list.</EmptyState>}
        {(unsectioned.length > 0 || sections.length > 0) && <section className={cn("grid gap-1.5 rounded-lg", sectionDrop === "" && "bg-accent ring-1 ring-primary")} aria-label="Unsectioned tasks" {...sectionDropTarget(null)}>{sections.length > 0 && groupHeading("Unsectioned")}{unsectioned.length > 0 ? taskList(unsectioned, "Unsectioned tasks") : <p className="px-2 py-1 text-muted-foreground italic">No unsectioned tasks.</p>}</section>}
        {sections.map(section => { const rows = sectionedTasks.filter(task => task.sectionId === section.id); return <section key={section.id} className={cn("grid gap-1.5 rounded-lg", sectionDrop === section.id && "bg-accent ring-1 ring-primary")} aria-label={`${section.title} tasks`} {...sectionDropTarget(section.id)}>{groupHeading(section.title, rows.length)}{rows.length === 0 ? <p className="px-2 py-1 text-muted-foreground italic">{completionFilter === "all" ? "No tasks in this section." : `No ${completionFilter} tasks in this section.`}</p> : taskList(rows, `${section.title} tasks`)}</section>; })}
        {unknownSections.map(id => <section key={id} className="grid gap-1.5" aria-label="Other section tasks">{groupHeading("Other section")}{taskList(sectionedTasks.filter(task => task.sectionId === id), "Other section tasks")}</section>)}
        {finishedTasks.length > 0 && <details className="group/done grid gap-1">
          <summary className="flex cursor-pointer list-none items-center border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase select-none [&::-webkit-details-marker]:hidden">
            <ChevronRight className="mr-1 size-3.5 transition-transform group-open/done:rotate-90"/>Finished<Count>{finishedTasks.length}</Count>
          </summary>
          <div className="pt-1">{taskList(finishedTasks, "Finished tasks")}</div>
        </details>}

        {containerId && <form className="flex gap-2" onSubmit={createSection}>
          <label htmlFor="new-section" className="sr-only">New section</label>
          <Input id="new-section" className="border-dashed bg-transparent focus-visible:border-solid" value={sectionTitle} onChange={e => setSectionTitle(e.target.value)} placeholder="New section" autoComplete="off"/>
          <Button variant="outline" disabled={busy || !sectionTitle.trim()}>Add section</Button>
        </form>}

        <DeletedItems noun="tasks" items={deleted} name={task => task.title} busy={busy} onRestore={task => void run(() => taskApi.restore({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }))}/>
  </>;
}
