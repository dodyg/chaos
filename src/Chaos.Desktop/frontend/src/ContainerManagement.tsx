import React from "react";
import { containerRpc, type ContainerResponse, type SectionResponse, type ProgressResponse } from "#neoastra";
import { useDragReorder } from "./dragReorder";
import { ArrowDown, ArrowUp, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Progress } from "@/components/ui/progress";
import { DeletedItems, Field, InlineError } from "@/components/common";

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
  const card = "gap-4 py-4";
  return <section className="grid gap-3" aria-label="Manage list or project">
    {error && <InlineError>{error}</InlineError>}
    <div className="grid grid-cols-[repeat(auto-fit,minmax(17rem,1fr))] gap-3">
      <Card className={card}>
        <CardHeader className="px-4"><CardTitle><h3>Details</h3></CardTitle></CardHeader>
        <CardContent className="grid gap-4 px-4">
          <form className="flex items-end gap-2" onSubmit={e => { e.preventDefault(); void run(() => api.rename({ operationId: id(), containerId: container.id, expectedVersion: container.version, name })); }}><Field label="Name" className="flex-1"><Input value={name} required onChange={e => setName(e.target.value)}/></Field><Button variant="outline" disabled={busy}>Rename</Button></form>
          {isProject && <form className="flex items-end gap-2" onSubmit={e => { e.preventDefault(); void run(() => api.setDeadline({ operationId: id(), containerId: container.id, expectedVersion: container.version, deadlineDate: deadline || null })); }}><Field label="Project deadline" className="flex-1"><Input type="date" value={deadline} onChange={e => setDeadline(e.target.value)}/></Field><Button variant="outline" disabled={busy}>Save deadline</Button></form>}
          {isProject && progress?.ok && <div className="grid gap-2"><Progress value={percent} aria-label="Task progress"/><p className="text-muted-foreground">Progress: {progress.doneCount} of {progress.totalCount} tasks; {progress.doneSubtaskCount} of {progress.totalSubtaskCount} subtasks done.</p></div>}
          <div className="flex flex-wrap items-center gap-2"><span className="text-muted-foreground">Position</span><Button variant="outline" size="sm" disabled={busy || index <= 1} onClick={() => void run(() => api.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index - 1 }))}><ArrowUp/>Move list up</Button><Button variant="outline" size="sm" disabled={busy || index === containers.length - 1} onClick={() => void run(() => api.reorder({ operationId: id(), containerId: container.id, expectedVersion: container.version, insertionIndex: index + 1 }))}><ArrowDown/>Move list down</Button></div>
        </CardContent>
      </Card>
      <Card className={card}>
        <CardHeader className="px-4"><CardTitle><h3>Sections</h3></CardTitle></CardHeader>
        <CardContent className="grid gap-3 px-4">
          {sections.length === 0 ? <p className="text-muted-foreground">No sections yet. Add one below the task list.</p> : <ol className="grid gap-1" aria-label="Sections">{sections.map((section, index) => <SectionRow key={section.id} section={section} index={index} count={sections.length} busy={busy} run={run} api={api} drag={drag} order={sections.map(row => row.id)} onDrop={(sectionId, insertionIndex) => { const moving = sections.find(row => row.id === sectionId)!; void run(() => api.reorderSection({ operationId: id(), sectionId: moving.id, expectedVersion: moving.version, title: moving.title, insertionIndex })); }}/>)}</ol>}
          <DeletedItems noun="sections" items={deletedSections} name={section => section.title} busy={busy} onRestore={section => void run(() => api.restoreSection({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title: section.title, insertionIndex: 0 }))}/>
        </CardContent>
      </Card>
      <Card className={`${card} border-destructive/30`}>
        <CardHeader className="px-4"><CardTitle className="text-destructive"><h3>Delete</h3></CardTitle></CardHeader>
        <CardContent className="px-4">
          <form className="flex flex-wrap items-end gap-2" onSubmit={e => { e.preventDefault(); void (async () => { setBusy(true); setError(null); try { const result = await api.delete({ operationId: id(), containerId: container.id, expectedVersion: container.version, contentsChoice }); if (!result.ok) throw new Error(result.error?.message ?? "Could not delete list."); await onDeleted(); } catch (err) { setError(err instanceof Error ? err.message : "Could not delete list."); } finally { setBusy(false); } })(); }}><Field label="When deleting this list" className="min-w-40 flex-1"><NativeSelect value={contentsChoice} onChange={e => setContentsChoice(e.target.value)}><NativeSelectOption value="MoveToInbox">Move its tasks to Inbox</NativeSelectOption><NativeSelectOption value="DeleteContents">Delete its tasks too</NativeSelectOption></NativeSelect></Field><Button variant="destructive" disabled={busy}>Delete list or project</Button></form>
        </CardContent>
      </Card>
    </div>
  </section>;
}

function SectionRow({ section, index, count, busy, run, api, drag, order, onDrop }: { section: SectionResponse; index: number; count: number; busy: boolean; run: (action: () => Promise<{ ok: boolean; error: { message: string } | null }>) => Promise<void>; api: typeof containerRpc; drag: ReturnType<typeof useDragReorder>; order: readonly string[]; onDrop: (sectionId: string, insertionIndex: number) => void }) {
  const [title, setTitle] = React.useState(section.title);
  React.useEffect(() => setTitle(section.title), [section.title]);
  const request = (insertionIndex: number) => ({ operationId: id(), sectionId: section.id, expectedVersion: section.version, title, insertionIndex });
  const icon = "text-muted-foreground";
  return <li className="group/row flex items-center gap-1" {...drag.target(order, index, "sections", busy, onDrop)}>{drag.handle(section.id, "sections", busy)}<form className="flex min-w-0 flex-1 items-center gap-1" onSubmit={e => { e.preventDefault(); void run(() => api.renameSection(request(index))); }}><label className="min-w-0 flex-1"><span className="sr-only">Section name</span><Input className="h-8" value={title} required onChange={e => setTitle(e.target.value)}/></label><div className="flex gap-0.5">{title !== section.title && <Button size="sm" variant="outline" disabled={busy}>Rename section</Button>}<Button type="button" variant="ghost" size="icon-sm" className={icon} title="Move up" aria-label="Move section up" disabled={busy || index === 0} onClick={() => void run(() => api.reorderSection(request(index - 1)))}><ArrowUp/></Button><Button type="button" variant="ghost" size="icon-sm" className={icon} title="Move down" aria-label="Move section down" disabled={busy || index === count - 1} onClick={() => void run(() => api.reorderSection(request(index + 1)))}><ArrowDown/></Button><Button type="button" variant="ghost" size="icon-sm" className={`${icon} hover:bg-destructive/10 hover:text-destructive`} title="Delete section" aria-label="Delete section" disabled={busy} onClick={() => void run(() => api.deleteSection(request(index)))}><Trash2/></Button></div></form></li>;
}
