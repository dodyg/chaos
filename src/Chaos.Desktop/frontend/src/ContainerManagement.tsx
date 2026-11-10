import React from "react";
import { containerRpc, type ContainerResponse, type SectionResponse, type ProgressResponse } from "#neoastra";

const id = () => crypto.randomUUID();
export function ContainerManagement({ container, containers, sections, taskRevision, onChanged, onDeleted, api = containerRpc }: { container: ContainerResponse; containers: ContainerResponse[]; sections: SectionResponse[]; taskRevision: string; onChanged: () => Promise<void>; onDeleted: () => Promise<void>; api?: typeof containerRpc }) {
  const [name, setName] = React.useState(container.name);
  const [deadline, setDeadline] = React.useState(container.deadlineDate ?? "");
  const [progress, setProgress] = React.useState<ProgressResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [deletedSections, setDeletedSections] = React.useState<SectionResponse[]>([]);
  const [contentsChoice, setContentsChoice] = React.useState("MoveToInbox");
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
  return <section aria-label="Manage list or project">
    {error && <p role="alert">{error}</p>}
    <form onSubmit={e => { e.preventDefault(); void run(() => api.rename({ operationId: id(), containerId: container.id, expectedVersion: container.version, name })); }}><label>Name<input value={name} required onChange={e => setName(e.target.value)}/></label><button disabled={busy}>Rename</button></form>
    <div className="row"><button disabled={busy || index <= 1} onClick={() => void run(() => api.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index - 1 }))}>Move list up</button><button disabled={busy || index === containers.length - 1} onClick={() => void run(() => api.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index + 1 }))}>Move list down</button></div>
    {container.kind === "Project" && <><form onSubmit={e => { e.preventDefault(); void run(() => api.setDeadline({ operationId: id(), containerId: container.id, expectedVersion: container.version, deadlineDate: deadline || null })); }}><label>Project deadline<input type="date" value={deadline} onChange={e => setDeadline(e.target.value)}/></label><button disabled={busy}>Save deadline</button></form>{progress?.ok && <p>Progress: {progress.doneCount} of {progress.totalCount} tasks; {progress.doneSubtaskCount} of {progress.totalSubtaskCount} subtasks done.</p>}</>}
    <h3>Sections</h3><ol>{sections.map((section, index) => <SectionRow key={section.id} section={section} index={index} count={sections.length} busy={busy} run={run} api={api}/>)}</ol>
    {deletedSections.length > 0 && <details><summary>Deleted sections</summary><ul>{deletedSections.map(section => <li key={section.id}>{section.title} <button type="button" disabled={busy} onClick={() => void run(() => api.restoreSection({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title: section.title, insertionIndex: 0 }))}>Restore</button></li>)}</ul></details>}
    <form onSubmit={e => { e.preventDefault(); void (async () => { setBusy(true); setError(null); try { const result = await api.delete({ operationId: id(), containerId: container.id, expectedVersion: container.version, contentsChoice }); if (!result.ok) throw new Error(result.error?.message ?? "Could not delete list."); await onDeleted(); } catch (err) { setError(err instanceof Error ? err.message : "Could not delete list."); } finally { setBusy(false); } })(); }}><label>When deleting this list<select value={contentsChoice} onChange={e => setContentsChoice(e.target.value)}><option value="MoveToInbox">Move its tasks to Inbox</option><option value="DeleteContents">Delete its tasks too</option></select></label><button disabled={busy}>Delete list or project</button></form>
  </section>;
}

function SectionRow({ section, index, count, busy, run, api }: { section: SectionResponse; index: number; count: number; busy: boolean; run: (action: () => Promise<{ ok: boolean; error: { message: string } | null }>) => Promise<void>; api: typeof containerRpc }) {
  const [title, setTitle] = React.useState(section.title);
  React.useEffect(() => setTitle(section.title), [section.title]);
  const request = (insertionIndex: number) => ({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title, insertionIndex });
  return <li><form onSubmit={e => { e.preventDefault(); void run(() => api.renameSection(request(index))); }}><label>Section name<input value={title} required onChange={e => setTitle(e.target.value)}/></label><div className="row"><button disabled={busy}>Rename section</button><button type="button" disabled={busy || index === 0} onClick={() => void run(() => api.reorderSection(request(index - 1)))}>Move section up</button><button type="button" disabled={busy || index === count - 1} onClick={() => void run(() => api.reorderSection(request(index + 1)))}>Move section down</button><button type="button" disabled={busy} onClick={() => void run(() => api.deleteSection(request(index)))}>Delete section</button></div></form></li>;
}
