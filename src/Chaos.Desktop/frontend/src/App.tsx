import React from "react";
import { taskRpc, subtaskRpc, containerRpc, type TaskResponse, type TaskOperationResponse, type SubtaskResponse, type SubtaskOperationResponse, type ContainerResponse, type SectionResponse } from "#neoastra";
import { Templates } from "./Templates";
import { ContainerManagement } from "./ContainerManagement";
import { createLatestRequest } from "./latestRequest";
import { useDragReorder } from "./dragReorder";

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
    setTasks([]); setDeleted([]); setSections([]); setEditing(null); setContainerId(id);
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
  function taskList(rows: TaskResponse[], name: string) {
    const order = rows.map(row => row.id);
    const byId = (id: string) => rows.find(row => row.id === id)!;
    return <ol className="tasks" aria-label={name}>{rows.map((task, index) => <li key={task.id}
      {...drag.target(order, index, `${task.containerId}|${task.sectionId ?? ""}`, busy, (id, insertionIndex) => reorderTask(byId(id), insertionIndex))}>
      <div>{drag.handle(task.id, `${task.containerId}|${task.sectionId ?? ""}`, busy)} <button className="task-title" disabled={busy} onClick={() => setEditing(task)}>{task.title}</button> <span>{label(task.status)}</span></div>
      <div className="row"><button disabled={busy || index === 0} aria-label={`Move ${task.title} up`} onClick={() => reorderTask(task, index - 1)}>↑</button>
      <button disabled={busy || index === rows.length - 1} aria-label={`Move ${task.title} down`} onClick={() => reorderTask(task, index + 1)}>↓</button>
      <button disabled={busy} onClick={() => void run(() => taskRpc.delete({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }), () => { if (editing?.id === task.id) setEditing(null); })}>Delete</button></div>
    </li>)}</ol>;
  }
  const unsectioned = tasks.filter(task => task.sectionId === null);
  const knownSectionIds = new Set(sections.map(section => section.id));
  const unknownSections = [...new Set(tasks.map(task => task.sectionId).filter((id): id is string => id !== null && !knownSectionIds.has(id)))];
  return <main>
    <h1>Chaos</h1>
    <form onSubmit={capture}><label htmlFor="capture">Add a task</label><div className="row"><input id="capture" value={title} onChange={e => setTitle(e.target.value)} autoComplete="off"/><button disabled={busy || !title.trim()}>Add task</button></div></form>
    {error && <p role="alert">{error}</p>}
    {warning && <p role="status">{warning}</p>}
    {deleted.length > 0 && <details><summary>Deleted tasks</summary><ul>{deleted.map(task => <li key={task.id}>{task.title} <button disabled={busy} onClick={() => void run(() => taskRpc.restore({ operationId: operationId(), taskId: task.id, expectedVersion: task.version }))}>Restore</button></li>)}</ul></details>}
    {deletedContainers.length > 0 && <details><summary>Deleted lists and projects</summary><ul>{deletedContainers.map(item => <li key={item.id}>{item.name} <button type="button" disabled={busy} onClick={() => void runContainer(() => containerRpc.restore({ operationId: operationId(), containerId: item.id, expectedVersion: item.version, name: item.name }), "Could not restore list.")}>Restore</button></li>)}</ul></details>}
    <label>List or project<select value={containerId} disabled={busy} onChange={e => selectContainer(e.target.value)}><option value="">Inbox</option>{containers.filter(c => c.kind !== "Inbox").map(c => <option key={c.id} value={c.id}>{c.name}{c.archived ? " (archived)" : ""}</option>)}</select></label>
    {containers.some(c => c.kind !== "Inbox") && <nav aria-label="Lists and projects"><ol className="tasks">{containers.map((c, index) => c.kind === "Inbox" ? null : <li key={c.id}
      {...drag.target(containers.map(row => row.id), index, "containers", busy, (id, insertionIndex) => { const moving = containers.find(row => row.id === id)!; void runContainer(() => containerRpc.reorder({ operationId: operationId(), containerId: moving.id, expectedVersion: moving.version, insertionIndex }), "Could not reorder lists."); })}>
      <div>{drag.handle(c.id, "containers", busy)} <button type="button" disabled={busy} aria-current={c.id === containerId ? "page" : undefined} onClick={() => selectContainer(c.id)}>{c.name}</button>{c.archived && <span> (archived)</span>}</div>
    </li>)}</ol></nav>}
    {containerId && containers.find(c => c.id === containerId) && <button type="button" disabled={busy} onClick={() => { const selected = containers.find(c => c.id === containerId)!; const request = { operationId: operationId(), containerId: selected.id, expectedVersion: selected.version, name: selected.name }; void runContainer(() => selected.archived ? containerRpc.unarchive(request) : containerRpc.archive(request), "Could not update archive state."); }}>{containers.find(c => c.id === containerId)?.archived ? "Unarchive" : "Archive"}</button>}
    <form onSubmit={e => { e.preventDefault(); void (async () => { setBusy(true); setError(null); try { const result = await containerRpc.create({ operationId: operationId(), name: containerName, kind: containerKind }); if (!result.ok || !result.container) throw new Error(result.error?.message ?? "Could not create list."); await refreshContainers(); selectContainer(result.container.id); setContainerName(""); } catch (err) { setError(String(err)); } finally { setBusy(false); } })(); }}><div className="row"><label>New list or project<input value={containerName} onChange={e => setContainerName(e.target.value)}/></label><label>Type<select value={containerKind} onChange={e => setContainerKind(e.target.value)}><option>List</option><option>Project</option></select></label><button disabled={busy || !containerName.trim()}>Create</button></div></form>
    {containerId && <form onSubmit={e => { e.preventDefault(); void (async () => { setBusy(true); try { const result = await containerRpc.createSection({ operationId: operationId(), containerId, title: sectionTitle }); if (!result.ok) throw new Error(result.error?.message ?? "Could not create section."); const list = await containerRpc.sections({ containerId }); if (!list.ok) throw new Error(list.error?.message ?? "Could not load sections."); setSections([...list.sections]); setSectionTitle(""); } catch (err) { setError(String(err)); } finally { setBusy(false); } })(); }}><div className="row"><label>New section<input value={sectionTitle} onChange={e => setSectionTitle(e.target.value)}/></label><button disabled={busy || !sectionTitle.trim()}>Add section</button></div></form>}
    {containerId && containers.find(c => c.id === containerId) && <ContainerManagement container={containers.find(c => c.id === containerId)!} containers={containers} sections={sections} taskRevision={`${subtaskRevision}|${tasks.map(t => `${t.id}:${t.version}`).join("|")}`} onDeleted={async () => { if (selectedContainer.current === containerId) selectContainer(""); await refreshContainers(); }} onChanged={async () => { await refreshContainers(); const result = await containerRpc.sections({ containerId }); if (selectedContainer.current !== containerId) return; if (!result.ok) throw new Error(result.error?.message ?? "Could not load sections."); setSections([...result.sections]); await refresh(); }}/>}
    <h2>{containers.find(c => c.id === containerId)?.name ?? "Inbox"}</h2>
    {tasks.length === 0 && <p>No tasks in this list.</p>}
    {unsectioned.length > 0 && <section aria-label="Unsectioned tasks">{sections.length > 0 && <h3>Unsectioned</h3>}{taskList(unsectioned, "Unsectioned tasks")}</section>}
    {sections.map(section => <section key={section.id} aria-label={`${section.title} tasks`}><h3>{section.title}</h3>{taskList(tasks.filter(task => task.sectionId === section.id), `${section.title} tasks`)}</section>)}
    {unknownSections.map(id => <section key={id} aria-label="Other section tasks"><h3>Other section</h3>{taskList(tasks.filter(task => task.sectionId === id), "Other section tasks")}</section>)}
    {editing && <section aria-labelledby="editor-heading"><h2 id="editor-heading">Edit task</h2><form onSubmit={save}>
      <label>Title<input required value={editing.title} onChange={e => change({ title: e.target.value })}/></label>
      <label>Notes<textarea rows={4} value={editing.notes} onChange={e => change({ notes: e.target.value })}/></label>
      <label>Status<select value={editing.status} disabled={busy} onChange={e => { const status = e.target.value; const hasOpen = subtasks.some(s => s.status !== "Done" && s.status !== "Cancelled"); const choice = status === "Done" && hasOpen ? (window.confirm("Complete open subtasks too? Choose Cancel to keep them open.") ? "CompleteAll" : "KeepOpen") : null; void run(() => taskRpc.setStatus({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, status, subtaskChoice: choice }), saved => { setEditing(current => current ? { ...current, status: saved.status, version: saved.version } : null); void refreshSubtasks(saved.id).then(setSubtasks).catch((e: unknown) => setError(String(e))); }); }}>{statuses.map(s => <option key={s} value={s}>{label(s)}</option>)}</select></label>
      <div className="grid">{(["planned", "due"] as const).map(kind => <fieldset key={kind}><legend>{label(kind)}</legend>
        <label>Date<input type="date" value={editing[`${kind}Date`] ?? ""} onChange={e => change({ [`${kind}Date`]: e.target.value || null })}/></label>
        <label>Time<input type="time" value={editing[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null })}/></label>
        <label>Time zone<input value={editing[`${kind}ZoneId`] ?? ""} onChange={e => change({ [`${kind}ZoneId`]: e.target.value || null })} placeholder="e.g. Africa/Cairo"/></label>
      </fieldset>)}</div>
      <label>Priority<select value={editing.priority} onChange={e => change({ priority: e.target.value })}>{["None", "Low", "Normal", "High", "Urgent"].map(s => <option key={s}>{s}</option>)}</select></label>
      <label>Estimate (minutes)<input type="number" min="0" value={editing.estimateMinutes ?? ""} onChange={e => change({ estimateMinutes: e.target.value === "" ? null : Number(e.target.value) })}/></label>
      <label>Category<select value={editing.category} onChange={e => change({ category: e.target.value })}><option>Personal</option><option>Work</option></select></label>
      <label>Move to list or project<select value={editing.containerId} disabled={busy} onChange={e => { const target = e.target.value; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: target, sectionId: null }), saved => { setEditing(current => current ? { ...current, containerId: saved.containerId, sectionId: null, version: saved.version } : null); }); }}>{containers.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
      {editing.containerId === containerId && sections.length > 0 && <label>Section<select value={editing.sectionId ?? ""} disabled={busy} onChange={e => { const sectionId = e.target.value || null; void run(() => containerRpc.moveTask({ operationId: operationId(), taskId: editing.id, expectedVersion: editing.version, containerId: editing.containerId, sectionId }), saved => setEditing(current => current ? { ...current, sectionId: saved.sectionId, version: saved.version } : null)); }}><option value="">No section</option>{sections.map(s => <option key={s.id} value={s.id}>{s.title}</option>)}</select></label>}
      <div className="row"><button disabled={busy}>Save</button><button type="button" onClick={() => setEditing(null)}>Cancel</button></div>
    </form>
      <h3>Subtasks</h3>
      <form onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.add({ operationId: operationId(), taskId: editing.id, title: subtaskTitle, notes: "" }), () => setSubtaskTitle("")); }}><label>New subtask<input value={subtaskTitle} onChange={e => setSubtaskTitle(e.target.value)}/></label><button disabled={busy || !subtaskTitle.trim()}>Add subtask</button></form>
      <ol className="tasks" aria-label="Subtasks">{subtasks.map((subtask, index) => <li key={subtask.id}
        {...drag.target(subtasks.map(row => row.id), index, `subtasks|${editing.id}`, busy, (id, insertionIndex) => reorderSubtask(subtasks.find(row => row.id === id)!, insertionIndex))}>
        <div>{drag.handle(subtask.id, `subtasks|${editing.id}`, busy)} <button disabled={busy} onClick={() => setSubtaskEdit(subtask)}>{subtask.title}</button> <span>{label(subtask.status)}</span></div><div className="row">
        <button disabled={busy || index === 0} aria-label={`Move ${subtask.title} up`} onClick={() => reorderSubtask(subtask, index - 1)}>↑</button>
        <button disabled={busy || index === subtasks.length - 1} aria-label={`Move ${subtask.title} down`} onClick={() => reorderSubtask(subtask, index + 1)}>↓</button>
        <button disabled={busy} onClick={() => void runSubtask(() => subtaskRpc.delete({ operationId: operationId(), subtaskId: subtask.id, expectedVersion: subtask.version, title: subtask.title, notes: subtask.notes }), () => { if (subtaskEdit?.id === subtask.id) setSubtaskEdit(null); })}>Delete</button>
      </div></li>)}</ol>
      {deletedSubtasks.length > 0 && <details><summary>Deleted subtasks</summary><ul>{deletedSubtasks.map(subtask => <li key={subtask.id}>{subtask.title} <button type="button" disabled={busy} onClick={() => void runSubtask(() => subtaskRpc.restore({ operationId: operationId(), taskId: editing.id, subtaskId: subtask.id, expectedVersion: subtask.version }))}>Restore</button></li>)}</ul></details>}
      {subtaskEdit && <form onSubmit={e => { e.preventDefault(); void runSubtask(() => subtaskRpc.edit({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, title: subtaskEdit.title, notes: subtaskEdit.notes }), () => setSubtaskEdit(null)); }}>
        <h4>Edit subtask</h4><label>Title<input required value={subtaskEdit.title} onChange={e => setSubtaskEdit({ ...subtaskEdit, title: e.target.value })}/></label>
        <label>Notes<textarea value={subtaskEdit.notes} onChange={e => setSubtaskEdit({ ...subtaskEdit, notes: e.target.value })}/></label>
        <label>Status<select value={subtaskEdit.status} disabled={busy} onChange={e => { const status = e.target.value; void runSubtask(() => subtaskRpc.setStatus({ operationId: operationId(), subtaskId: subtaskEdit.id, expectedVersion: subtaskEdit.version, status }), saved => setSubtaskEdit(current => current ? { ...current, status: saved.status, version: saved.version } : null)); }}>{statuses.map(s => <option key={s} value={s}>{label(s)}</option>)}</select></label>
        <div className="row"><button disabled={busy}>Save subtask</button><button type="button" onClick={() => setSubtaskEdit(null)}>Cancel</button></div>
      </form>}
    </section>}
    {templates ?? <Templates containerId={containerId} onCreated={refresh}/>}
  </main>;
}

