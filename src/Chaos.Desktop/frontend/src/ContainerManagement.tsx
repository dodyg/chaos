import React from "react";
import { containerRpc, type ContainerResponse, type SectionResponse, type ProgressResponse } from "#neoastra";

const id = () => crypto.randomUUID();
export function ContainerManagement({ container, containers, sections, taskRevision, onChanged }: { container: ContainerResponse; containers: ContainerResponse[]; sections: SectionResponse[]; taskRevision: string; onChanged: () => Promise<void> }) {
  const [name, setName] = React.useState(container.name);
  const [deadline, setDeadline] = React.useState(container.deadlineDate ?? "");
  const [progress, setProgress] = React.useState<ProgressResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  React.useEffect(() => { setName(container.name); setDeadline(container.deadlineDate ?? ""); }, [container]);
  React.useEffect(() => {
    let active = true;
    if (container.kind === "Project") void containerRpc.progress({ containerId: container.id }).then(result => { if (active) setProgress(result); }).catch((e: unknown) => { if (active) setError(String(e)); });
    else setProgress(null);
    return () => { active = false; };
  }, [container, sections, taskRevision]);
  async function run(action: () => Promise<{ ok: boolean; error: { message: string } | null }>) {
    setBusy(true); setError(null);
    try { const result = await action(); if (!result.ok) throw new Error(result.error?.message ?? "Could not update this list."); await onChanged(); }
    catch (e) { setError(e instanceof Error ? e.message : "Could not update this list."); }
    finally { setBusy(false); }
  }
  const index = containers.findIndex(c => c.id === container.id);
  return <section aria-label="Manage list or project">
    {error && <p role="alert">{error}</p>}
    <form onSubmit={e => { e.preventDefault(); void run(() => containerRpc.rename({ operationId: id(), containerId: container.id, expectedVersion: container.version, name })); }}><label>Name<input value={name} required onChange={e => setName(e.target.value)}/></label><button disabled={busy}>Rename</button></form>
    <div className="row"><button disabled={busy || index <= 1} onClick={() => void run(() => containerRpc.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index - 1 }))}>Move list up</button><button disabled={busy || index === containers.length - 1} onClick={() => void run(() => containerRpc.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index + 1 }))}>Move list down</button></div>
    {container.kind === "Project" && <><form onSubmit={e => { e.preventDefault(); void run(() => containerRpc.setDeadline({ operationId: id(), containerId: container.id, expectedVersion: container.version, deadlineDate: deadline || null })); }}><label>Project deadline<input type="date" value={deadline} onChange={e => setDeadline(e.target.value)}/></label><button disabled={busy}>Save deadline</button></form>{progress?.ok && <p>Progress: {progress.doneCount} of {progress.totalCount} tasks; {progress.doneSubtaskCount} of {progress.totalSubtaskCount} subtasks done.</p>}</>}
    <h3>Sections</h3><ol>{sections.map((section, index) => <SectionRow key={section.id} section={section} index={index} count={sections.length} busy={busy} run={run}/>)}</ol>
  </section>;
}

function SectionRow({ section, index, count, busy, run }: { section: SectionResponse; index: number; count: number; busy: boolean; run: (action: () => Promise<{ ok: boolean; error: { message: string } | null }>) => Promise<void> }) {
  const [title, setTitle] = React.useState(section.title);
  React.useEffect(() => setTitle(section.title), [section.title]);
  const request = (insertionIndex: number) => ({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title, insertionIndex });
  return <li><form onSubmit={e => { e.preventDefault(); void run(() => containerRpc.renameSection(request(index))); }}><label>Section name<input value={title} required onChange={e => setTitle(e.target.value)}/></label><div className="row"><button disabled={busy}>Rename section</button><button type="button" disabled={busy || index === 0} onClick={() => void run(() => containerRpc.reorderSection(request(index - 1)))}>Move section up</button><button type="button" disabled={busy || index === count - 1} onClick={() => void run(() => containerRpc.reorderSection(request(index + 1)))}>Move section down</button></div></form></li>;
}
