import React from "react";
import { containerRpc, type ContainerResponse, type SectionResponse, type ProgressResponse } from "#neoastra";
import { useDragReorder } from "./dragReorder";
import { Icon } from "./Icon";

const id = () => crypto.randomUUID();
export function ContainerManagement({ container, containers, sections, taskRevision, onChanged, onDeleted, api = containerRpc }: { container: ContainerResponse; containers: ContainerResponse[]; sections: SectionResponse[]; taskRevision: string; onChanged: () => Promise<void>; onDeleted: () => Promise<void>; api?: typeof containerRpc }) {
  const [name, setName] = React.useState(container.name);
  const [deadline, setDeadline] = React.useState(container.deadlineDate ?? "");
  const [progress, setProgress] = React.useState<ProgressResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [deletedSections, setDeletedSections] = React.useState<SectionResponse[]>([]);
  const [contentsChoice, setContentsChoice] = React.useState("MoveToInbox");
  const drag = useDragReorder();
  React.useEffect(() => { setName(container.name); setDeadline(container.deadlineDate ?? ""); }, [container]);
  React.useEffect(() => {
    let active = true;
    if (container.kind === "Project") void api.progress({ containerId: container.id }).then(result => { if (active) setProgress(result); }).catch((e: unknown) => { if (active) setError(String(e)); });
    else setProgress(null);
    return () => { active = false; };
  }, [container, sections, taskRevision]);
  async function loadDeletedSections() {
    const result = await api.deletedSections({ containerId: container.id });
    if (!result.ok) throw new Error(result.error?.message ?? "Could not load deleted sections.");
    setDeletedSections([...result.sections]);
  }
  React.useEffect(() => { void loadDeletedSections().catch((e: unknown) => setError(String(e))); }, [container.id, sections]);
  async function run(action: () => Promise<{ ok: boolean; error: { message: string } | null }>) {
    setBusy(true); setError(null);
    try { const result = await action(); if (!result.ok) throw new Error(result.error?.message ?? "Could not update this list."); await onChanged(); }
    catch (e) { setError(e instanceof Error ? e.message : "Could not update this list."); }
    finally { setBusy(false); }
  }
  const index = containers.findIndex(c => c.id === container.id);
  const isProject = container.kind === "Project";
  const percent = progress?.ok && progress.totalCount > 0 ? Math.round(progress.doneCount / progress.totalCount * 100) : 0;
  return <section className="manage" aria-label="Manage list or project">
    {error && <p className="banner error" role="alert">{error}</p>}
    <div className="manage-grid">
      <div className="card stack">
        <h3>Details</h3>
        <form className="inline-form" onSubmit={e => { e.preventDefault(); void run(() => api.rename({ operationId: id(), containerId: container.id, expectedVersion: container.version, name })); }}><label className="field grow">Name<input value={name} required onChange={e => setName(e.target.value)}/></label><button disabled={busy}>Rename</button></form>
        {isProject && <form className="inline-form" onSubmit={e => { e.preventDefault(); void run(() => api.setDeadline({ operationId: id(), containerId: container.id, expectedVersion: container.version, deadlineDate: deadline || null })); }}><label className="field grow">Project deadline<input type="date" value={deadline} onChange={e => setDeadline(e.target.value)}/></label><button disabled={busy}>Save deadline</button></form>}
        {isProject && progress?.ok && <div className="progress-block"><div className="progress" role="progressbar" aria-label="Task progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}><span style={{ width: `${percent}%` }}/></div><p className="subtle">Progress: {progress.doneCount} of {progress.totalCount} tasks; {progress.doneSubtaskCount} of {progress.totalSubtaskCount} subtasks done.</p></div>}
        <div className="button-row"><span className="subtle">Position</span><button disabled={busy || index <= 1} onClick={() => void run(() => api.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index - 1 }))}><Icon name="up"/>Move list up</button><button disabled={busy || index === containers.length - 1} onClick={() => void run(() => api.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index + 1 }))}><Icon name="down"/>Move list down</button></div>
      </div>
      <div className="card stack">
        <h3>Sections</h3>
        {sections.length === 0 ? <p className="subtle">No sections yet. Add one below the task list.</p> : <ol className="section-rows" aria-label="Sections">{sections.map((section, index) => <SectionRow key={section.id} section={section} index={index} count={sections.length} busy={busy} run={run} api={api} drag={drag} order={sections.map(row => row.id)} onDrop={(sectionId, insertionIndex) => { const moving = sections.find(row => row.id === sectionId)!; void run(() => api.reorderSection({ operationId: id(), sectionId: moving.id, expectedVersion: moving.version, title: moving.title, insertionIndex })); }}/>)}</ol>}
        {deletedSections.length > 0 && <details className="trash"><summary>Deleted sections ({deletedSections.length})</summary><ul className="trash-list">{deletedSections.map(section => <li key={section.id}><span>{section.title}</span> <button type="button" className="link-button" disabled={busy} onClick={() => void run(() => api.restoreSection({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title: section.title, insertionIndex: 0 }))}>Restore</button></li>)}</ul></details>}
      </div>
      <div className="card stack danger-zone">
        <h3>Delete</h3>
        <form className="inline-form" onSubmit={e => { e.preventDefault(); void (async () => { setBusy(true); setError(null); try { const result = await api.delete({ operationId: id(), containerId: container.id, expectedVersion: container.version, contentsChoice }); if (!result.ok) throw new Error(result.error?.message ?? "Could not delete list."); await onDeleted(); } catch (err) { setError(err instanceof Error ? err.message : "Could not delete list."); } finally { setBusy(false); } })(); }}><label className="field grow">When deleting this list<select value={contentsChoice} onChange={e => setContentsChoice(e.target.value)}><option value="MoveToInbox">Move its tasks to Inbox</option><option value="DeleteContents">Delete its tasks too</option></select></label><button className="danger" disabled={busy}>Delete list or project</button></form>
      </div>
    </div>
  </section>;
}

function SectionRow({ section, index, count, busy, run, api, drag, order, onDrop }: { section: SectionResponse; index: number; count: number; busy: boolean; run: (action: () => Promise<{ ok: boolean; error: { message: string } | null }>) => Promise<void>; api: typeof containerRpc; drag: ReturnType<typeof useDragReorder>; order: readonly string[]; onDrop: (sectionId: string, insertionIndex: number) => void }) {
  const [title, setTitle] = React.useState(section.title);
  React.useEffect(() => setTitle(section.title), [section.title]);
  const request = (insertionIndex: number) => ({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title, insertionIndex });
  return <li className="row-item" {...drag.target(order, index, "sections", busy, onDrop)}>{drag.handle(section.id, "sections", busy)}<form className="section-form" onSubmit={e => { e.preventDefault(); void run(() => api.renameSection(request(index))); }}><label className="grow"><span className="sr-only">Section name</span><input value={title} required onChange={e => setTitle(e.target.value)}/></label><div className="row-actions visible">{title !== section.title && <button disabled={busy}>Rename section</button>}<button type="button" className="icon-button" title="Move up" aria-label="Move section up" disabled={busy || index === 0} onClick={() => void run(() => api.reorderSection(request(index - 1)))}><Icon name="up"/></button><button type="button" className="icon-button" title="Move down" aria-label="Move section down" disabled={busy || index === count - 1} onClick={() => void run(() => api.reorderSection(request(index + 1)))}><Icon name="down"/></button><button type="button" className="icon-button danger" title="Delete section" aria-label="Delete section" disabled={busy} onClick={() => void run(() => api.deleteSection(request(index)))}><Icon name="trash"/></button></div></form></li>;
}
