import React from "react";
import { taskRpc, subtaskRpc, containerRpc, type TaskResponse, type TaskOperationResponse, type SubtaskResponse, type SubtaskOperationResponse, type ContainerResponse, type SectionResponse } from "#neoastra";
import { Templates } from "./Templates";
import { ContainerManagement } from "./ContainerManagement";
import { createLatestRequest } from "./latestRequest";
import { useDragReorder } from "./dragReorder";
import { Icon } from "./Icon";

const statuses = ["NotStarted", "InProgress", "WaitingOnSomeone", "NeedsReview", "Done", "Cancelled"];
const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");

export function App({ api = { taskRpc, containerRpc }, templates }: { api?: { taskRpc: typeof taskRpc; containerRpc: typeof containerRpc }; templates?: React.ReactNode }) {
  const { taskRpc, containerRpc } = api;
  const [title, setTitle] = React.useState("");
  const [tasks, setTasks] = React.useState<TaskResponse[]>([]);
  const [editing, setEditing] = React.useState<TaskResponse | null>(null);
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
  const [view, setView] = React.useState<"tasks" | "templates">("tasks");
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
  React.useEffect(() => { void refreshContainers().catch((e: unknown) => setError(String(e))); }, []);
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
    } catch (e) { setError(e instanceof Error ? e.message : "The change could not be saved."); }
    finally { setBusy(false); }
  }
  const operationId = () => crypto.randomUUID();
  function capture(event: React.FormEvent) {
    event.preventDefault();
    void run(() => taskRpc.create({ operationId: operationId(), title, notes: null, containerId: containerId || null }), () => setTitle(""));
  }
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
  function reorderTask(task: TaskResponse, insertionIndex: number) {
    void run(() => taskRpc.reorder({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, insertionIndex }));
  }
  function reorderSubtask(subtask: SubtaskResponse, insertionIndex: number) {
    void runSubtask(() => subtaskRpc.reorder({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, insertionIndex }));
  }
  async function runContainer(action: () => Promise<{ ok: boolean; error: { message: string } | null }>, failure: string) {
    if (busy) return;
    setBusy(true); setError(null);
    try { const result = await action(); if (!result.ok) throw new Error(result.error?.message ?? failure); await refreshContainers(); }
    catch (e) { setError(e instanceof Error ? e.message : failure); }
    finally { setBusy(false); }
  }
  function openContainer(id: string) {
    setView("tasks");
    if (id !== containerId) selectContainer(id);
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
  function taskMeta(task: TaskResponse) {
    const parts: React.ReactNode[] = [];
    if (task.dueDate) parts.push(<span key="due" className="meta-item"><Icon name="calendar" size={13}/>Due {task.dueDate}{task.dueTime ? ` ${task.dueTime}` : ""}</span>);
    else if (task.plannedDate) parts.push(<span key="planned" className="meta-item"><Icon name="calendar" size={13}/>Planned {task.plannedDate}</span>);
    if (task.estimateMinutes) parts.push(<span key="estimate" className="meta-item"><Icon name="clock" size={13}/>{task.estimateMinutes} min</span>);
    if (task.priority === "High" || task.priority === "Urgent") parts.push(<span key="priority" className={`pill priority-${task.priority}`}>{task.priority}</span>);
    return parts;
  }
  function taskList(rows: TaskResponse[], name: string) {
    const order = rows.map(row => row.id);
    const byId = (id: string) => rows.find(row => row.id === id)!;
    return <ol className="rows" aria-label={name}>{rows.map((task, index) => <li key={task.id} className={`row-item${editing?.id === task.id ? " selected" : ""}${task.status === "Done" || task.status === "Cancelled" ? " finished" : ""}`}
      {...drag.target(order, index, `${task.containerId}|${task.sectionId ?? ""}`, busy, (id, insertionIndex) => reorderTask(byId(id), insertionIndex))}>
      {drag.handle(task.id, `${task.containerId}|${task.sectionId ?? ""}`, busy)}
      <div className="row-main">
        <button className="row-title" disabled={busy} onClick={() => setEditing(task)}>{task.title}</button>
        {taskMeta(task).length > 0 && <div className="row-meta">{taskMeta(task)}</div>}
      </div>
      {task.status !== "NotStarted" && <span className={`pill status-${task.status}`}>{label(task.status)}</span>}
      <div className="row-actions">
        <button className="icon-button" disabled={busy || index === 0} aria-label={`Move ${task.title} up`} title="Move up" onClick={() => reorderTask(task, index - 1)}><Icon name="up"/></button>
        <button className="icon-button" disabled={busy || index === rows.length - 1} aria-label={`Move ${task.title} down`} title="Move down" onClick={() => reorderTask(task, index + 1)}><Icon name="down"/></button>
        <button className="icon-button danger" disabled={busy} aria-label={`Delete ${task.title}`} title="Delete" onClick={() => void run(() => taskRpc.delete({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); })}><Icon name="trash"/></button>
      </div>
    </li>)}</ol>;
  }
  const unsectioned = tasks.filter(task => task.sectionId === null);
  const knownSectionIds = new Set(sections.map(section => section.id));
  const unknownSections = [...new Set(tasks.map(task => task.sectionId).filter((id): id is string => id !== null && !knownSectionIds.has(id)))];
  const selected = containers.find(c => c.id === containerId);
  const listName = selected?.name ?? "Inbox";
  const openCount = tasks.filter(t => t.status !== "Done" && t.status !== "Cancelled").length;
  const userContainers = containers.filter(c => c.kind !== "Inbox");

  return <div className={`app-shell${editing && view === "tasks" ? " has-detail" : ""}`} aria-busy={busy}>
    <aside className="sidebar">
      <h1 className="brand"><span className="brand-mark" aria-hidden="true">✷</span>Chaos</h1>
      <nav aria-label="Views" className="nav-group">
        <button type="button" className="nav-item" aria-current={view === "tasks" && !containerId ? "page" : undefined} onClick={() => openContainer("")}><Icon name="inbox"/>Inbox</button>
        <button type="button" className="nav-item" aria-current={view === "templates" ? "page" : undefined} onClick={() => setView("templates")}><Icon name="template"/>Templates</button>
      </nav>
      <div className="nav-heading">Lists &amp; projects</div>
      {userContainers.length > 0
        ? <nav aria-label="Lists and projects" className="nav-group"><ol className="nav-list">{containers.map((c, index) => c.kind === "Inbox" ? null : <li key={c.id} className={c.archived ? "archived" : undefined}
            {...drag.target(containers.map(row => row.id), index, "containers", busy, (id, insertionIndex) => { const moving = containers.find(row => row.id === id)!; void runContainer(() => containerRpc.reorder({ operationId: operationId(), containerId: moving.id, expectedVersion: moving.version, insertionIndex }), "Could not reorder lists."); })}>
            {drag.handle(c.id, "containers", busy)}
            <button type="button" className="nav-item" disabled={busy} aria-current={view === "tasks" && c.id === containerId ? "page" : undefined} onClick={() => openContainer(c.id)}><Icon name={c.kind === "Project" ? "project" : "list"}/>{c.name}</button>
            {c.archived && <span className="nav-tag">archived</span>}
          </li>)}</ol></nav>
        : <p className="nav-empty">No lists yet. Create one below.</p>}
      <form className="new-list" onSubmit={createContainer}>
        <label className="sr-only" htmlFor="new-container">New list or project</label>
        <input id="new-container" placeholder="New list or project" value={containerName} onChange={e => setContainerName(e.target.value)} autoComplete="off"/>
        <div className="new-list-actions">
          <label className="sr-only" htmlFor="new-container-kind">Type</label>
          <select id="new-container-kind" value={containerKind} onChange={e => setContainerKind(e.target.value)}><option>List</option><option>Project</option></select>
          <button className="primary" disabled={busy || !containerName.trim()}>Create</button>
        </div>
      </form>
      {deletedContainers.length > 0 && <details className="trash"><summary>Deleted lists ({deletedContainers.length})</summary><ul className="trash-list">{deletedContainers.map(item => <li key={item.id}><span>{item.name}</span> <button type="button" className="link-button" disabled={busy} onClick={() => void runContainer(() => containerRpc.restore({ operationId: operationId(), containerId: item.id, expectedVersion: item.version, name: item.name }), "Could not restore list.")}>Restore</button></li>)}</ul></details>}
    </aside>

    <div className="content">
      {error && <div className="banner error" role="alert"><span>{error}</span><button type="button" className="icon-button" aria-label="Dismiss error" onClick={() => setError(null)}><Icon name="close"/></button></div>}
      {warning && <div className="banner warning" role="status"><span>{warning}</span><button type="button" className="icon-button" aria-label="Dismiss warning" onClick={() => setWarning(null)}><Icon name="close"/></button></div>}

      {view === "templates" ? (templates ?? <Templates containerId={containerId} containerName={listName} onCreated={refresh}/>) : <>
        <header className="page-header">
          <div>
            <p className="eyebrow">{selected ? selected.kind : "Inbox"}{selected?.archived ? " · archived" : ""}{selected?.deadlineDate ? ` · due ${selected.deadlineDate}` : ""}</p>
            <h2>{listName}</h2>
            <p className="subtle">{tasks.length === 0 ? "Nothing here yet" : `${openCount} open · ${tasks.length - openCount} finished`}</p>
          </div>
          {selected && <div className="header-actions">
            <button type="button" disabled={busy} onClick={() => { const request = { operationId: operationId(), containerId: selected.id, expectedVersion: selected.version, name: selected.name }; void runContainer(() => selected.archived ? containerRpc.unarchive(request) : containerRpc.archive(request), "Could not update archive state."); }}><Icon name="archive"/>{selected.archived ? "Unarchive" : "Archive"}</button>
            <button type="button" aria-expanded={showSettings} aria-controls="list-settings" onClick={() => setShowSettings(v => !v)}><Icon name="settings"/>{showSettings ? "Hide settings" : "Settings"}</button>
          </div>}
        </header>

        {selected && showSettings && <div id="list-settings" className="settings-panel"><ContainerManagement container={selected} containers={containers} sections={sections} taskRevision={`${subtaskRevision}|${tasks.map(t => `${t.id}:${t.version}`).join("|")}`} onDeleted={async () => { if (selectedContainer.current === containerId) selectContainer(""); await refreshContainers(); }} onChanged={async () => { await refreshContainers(); const result = await containerRpc.sections({ containerId }); if (selectedContainer.current !== containerId) return; if (!result.ok) throw new Error(result.error?.message ?? "Could not load sections."); setSections([...result.sections]); await refresh(); }}/></div>}

        <form className="capture" onSubmit={capture}>
          <label htmlFor="capture" className="sr-only">Add a task</label>
          <Icon name="plus"/>
          <input id="capture" value={title} onChange={e => setTitle(e.target.value)} autoComplete="off" placeholder={`Add a task to ${listName}`}/>
          <button className="primary" disabled={busy || !title.trim()}>Add task</button>
        </form>

        {tasks.length === 0 && <div className="empty-state"><p className="empty-title">No tasks in this list.</p><p className="subtle">Type above and press Enter to capture one.</p></div>}
        {unsectioned.length > 0 && <section className="group" aria-label="Unsectioned tasks">{sections.length > 0 && <h3 className="group-heading">Unsectioned</h3>}{taskList(unsectioned, "Unsectioned tasks")}</section>}
        {sections.map(section => { const rows = tasks.filter(task => task.sectionId === section.id); return <section key={section.id} className="group" aria-label={`${section.title} tasks`}><h3 className="group-heading">{section.title}<span className="count">{rows.length}</span></h3>{rows.length === 0 ? <p className="group-empty">No tasks in this section.</p> : taskList(rows, `${section.title} tasks`)}</section>; })}
        {unknownSections.map(id => <section key={id} className="group" aria-label="Other section tasks"><h3 className="group-heading">Other section</h3>{taskList(tasks.filter(task => task.sectionId === id), "Other section tasks")}</section>)}

        {containerId && <form className="inline-add" onSubmit={createSection}>
          <label htmlFor="new-section" className="sr-only">New section</label>
          <input id="new-section" value={sectionTitle} onChange={e => setSectionTitle(e.target.value)} placeholder="New section" autoComplete="off"/>
          <button disabled={busy || !sectionTitle.trim()}>Add section</button>
        </form>}

        {deleted.length > 0 && <details className="trash"><summary>Deleted tasks ({deleted.length})</summary><ul className="trash-list">{deleted.map(task => <li key={task.id}><span>{task.title}</span> <button type="button" className="link-button" disabled={busy} onClick={() => void run(() => taskRpc.restore({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }))}>Restore</button></li>)}</ul></details>}
      </>}
    </div>

    {editing && view === "tasks" && <aside className="detail" aria-labelledby="editor-heading">
      <div className="detail-header">
        <h2 id="editor-heading">Edit task</h2>
        <button type="button" className="icon-button" aria-label="Close task editor" onClick={() => setEditing(null)}><Icon name="close" size={18}/></button>
      </div>
      <div className="detail-body">
        <form className="stack" onSubmit={save}>
          <label className="field">Title<input required className="title-input" value={editing.title} onChange={e => change({ title: e.target.value })}/></label>
          <label className="field">Notes<textarea rows={4} value={editing.notes} onChange={e => change({ notes: e.target.value })} placeholder="Add details…"/></label>
          <div className="field-grid">
            <label className="field">Status<select value={editing.status} disabled={busy} onChange={e => { const status = e.target.value; const hasOpen = subtasks.some(s => s.status !== "Done" && s.status !== "Cancelled"); const choice = status === "Done" && hasOpen ? (window.confirm("Complete open subtasks too? Choose Cancel to keep them open.") ? "CompleteAll" : "KeepOpen") : null; void run(() => taskRpc.setStatus({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, status, subtaskChoice: choice }), saved => { setEditing(current => current ? { ...current, status: saved.status, version: saved.version } : null); void refreshSubtasks(saved.id).then(setSubtasks).catch((e: unknown) => setError(String(e))); }); }}>{statuses.map(s => <option key={s} value={s}>{label(s)}</option>)}</select></label>
            <label className="field">Priority<select value={editing.priority} onChange={e => change({ priority: e.target.value })}>{["None", "Low", "Normal", "High", "Urgent"].map(s => <option key={s}>{s}</option>)}</select></label>
            <label className="field">Category<select value={editing.category} onChange={e => change({ category: e.target.value })}><option>Personal</option><option>Work</option></select></label>
            <label className="field">Estimate (minutes)<input type="number" min="0" value={editing.estimateMinutes ?? ""} onChange={e => change({ estimateMinutes: e.target.value === "" ? null : Number(e.target.value) })}/></label>
          </div>
          {(["planned", "due"] as const).map(kind => <fieldset key={kind} className="when"><legend>{label(kind)}</legend>
            <div className="field-grid three">
              <label className="field">Date<input type="date" value={editing[`${kind}Date`] ?? ""} onChange={e => change({ [`${kind}Date`]: e.target.value || null })}/></label>
              <label className="field">Time<input type="time" value={editing[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null })}/></label>
              <label className="field">Time zone<input value={editing[`${kind}ZoneId`] ?? ""} onChange={e => change({ [`${kind}ZoneId`]: e.target.value || null })} placeholder="e.g. Africa/Cairo"/></label>
            </div>
          </fieldset>)}
          <div className="field-grid">
            <label className="field">Move to list or project<select value={editing.containerId} disabled={busy} onChange={e => { const target = e.target.value; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: target, sectionId: null }), saved => { setEditing(current => current ? { ...current, containerId: saved.containerId, sectionId: null, version: saved.version } : null); }); }}>{containers.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
            {editing.containerId === containerId && sections.length > 0 && <label className="field">Section<select value={editing.sectionId ?? ""} disabled={busy} onChange={e => { const sectionId = e.target.value || null; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: editing.containerId, sectionId }), saved => setEditing(current => current ? { ...current, sectionId: saved.sectionId, version: saved.version } : null)); }}><option value="">No section</option>{sections.map(s => <option key={s.id} value={s.id}>{s.title}</option>)}</select></label>}
          </div>
          <div className="form-actions"><button className="primary" disabled={busy}>Save</button><button type="button" onClick={() => setEditing(null)}>Cancel</button></div>
        </form>

        <section className="subtasks" aria-labelledby="subtasks-heading">
          <h3 id="subtasks-heading">Subtasks{subtasks.length > 0 && <span className="count">{subtasks.filter(s => s.status === "Done").length}/{subtasks.length}</span>}</h3>
          {subtasks.length > 0 && <ol className="rows compact" aria-label="Subtasks">{subtasks.map((subtask, index) => <li key={subtask.id} className={`row-item${subtaskEdit?.id === subtask.id ? " selected" : ""}${subtask.status === "Done" || subtask.status === "Cancelled" ? " finished" : ""}`}
            {...drag.target(subtasks.map(row => row.id), index, `subtasks|${editing.id}`, busy, (id, insertionIndex) => reorderSubtask(subtasks.find(row => row.id === id)!, insertionIndex))}>
            {drag.handle(subtask.id, `subtasks|${editing.id}`, busy)}
            <div className="row-main"><button className="row-title" disabled={busy} onClick={() => setSubtaskEdit(subtask)}>{subtask.title}</button></div>
            {subtask.status !== "NotStarted" && <span className={`pill status-${subtask.status}`}>{label(subtask.status)}</span>}
            <div className="row-actions">
              <button className="icon-button" disabled={busy || index === 0} aria-label={`Move ${subtask.title} up`} title="Move up" onClick={() => reorderSubtask(subtask, index - 1)}><Icon name="up"/></button>
              <button className="icon-button" disabled={busy || index === subtasks.length - 1} aria-label={`Move ${subtask.title} down`} title="Move down" onClick={() => reorderSubtask(subtask, index + 1)}><Icon name="down"/></button>
              <button className="icon-button danger" disabled={busy} aria-label={`Delete ${subtask.title}`} title="Delete" onClick={() => void runSubtask(() => subtaskRpc.delete({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, title: subtask.title, notes: subtask.notes }), () => { if (subtaskEdit?.id === subtask.id) setSubtaskEdit(null); })}><Icon name="trash"/></button>
            </div>
          </li>)}</ol>}
          <form className="inline-add" onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.add({ operationId: operationId(), taskId: editing.id, title: subtaskTitle, notes: "" }), () => setSubtaskTitle("")); }}>
            <label htmlFor="new-subtask" className="sr-only">New subtask</label>
            <input id="new-subtask" value={subtaskTitle} onChange={e => setSubtaskTitle(e.target.value)} placeholder="Add a subtask" autoComplete="off"/>
            <button disabled={busy || !subtaskTitle.trim()}>Add subtask</button>
          </form>
          {subtaskEdit && <form className="stack card" onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.edit({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, title: subtaskEdit.title, notes: subtaskEdit.notes }), () => setSubtaskEdit(null)); }}>
            <h4>Edit subtask</h4>
            <label className="field">Title<input required value={subtaskEdit.title} onChange={e => setSubtaskEdit({ ...subtaskEdit, title: e.target.value })}/></label>
            <label className="field">Notes<textarea value={subtaskEdit.notes} onChange={e => setSubtaskEdit({ ...subtaskEdit, notes: e.target.value })}/></label>
            <label className="field">Status<select value={subtaskEdit.status} disabled={busy} onChange={e => { const status = e.target.value; void runSubtask(() => subtaskRpc.setStatus({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, status }), saved => setSubtaskEdit(current => current ? { ...current, status: saved.status, version: saved.version } : null)); }}>{statuses.map(s => <option key={s} value={s}>{label(s)}</option>)}</select></label>
            <div className="form-actions"><button className="primary" disabled={busy}>Save subtask</button><button type="button" onClick={() => setSubtaskEdit(null)}>Cancel</button></div>
          </form>}
          {deletedSubtasks.length > 0 && <details className="trash"><summary>Deleted subtasks ({deletedSubtasks.length})</summary><ul className="trash-list">{deletedSubtasks.map(subtask => <li key={subtask.id}><span>{subtask.title}</span> <button type="button" className="link-button" disabled={busy} onClick={() => void runSubtask(() => subtaskRpc.restore({ operationId: operationId(), taskId: editing.id, subtaskId: subtask.id, expectedVersion: subtask.version }))}>Restore</button></li>)}</ul></details>}
        </section>
      </div>
    </aside>}
  </div>;
}
