import React from "react";
import { tagRpc, type TagResponse } from "#neoastra";
import { Hash, Plus, Trash2, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, InlineError } from "@/components/common";

/** The fixed tag palette (PLAN §6); keys match TagColors on the server. */
export const tagColors = ["slate", "red", "orange", "amber", "green", "teal", "blue", "violet", "pink"] as const;

const dot: Record<string, string> = {
  slate: "bg-slate-500", red: "bg-red-600", orange: "bg-orange-500", amber: "bg-amber-500", green: "bg-green-600",
  teal: "bg-teal-600", blue: "bg-blue-600", violet: "bg-violet-600", pink: "bg-pink-600",
};

/** MIME type carried by a dragged task so a tag can accept it (drag-to-tag). */
export const taskDragType = "application/x-chaos-task";

const operationId = () => crypto.randomUUID();

export function TagDot({ color }: { color: string | null }) {
  return <span aria-hidden="true" className={cn("inline-block size-2 shrink-0 rounded-full", color ? dot[color] : "bg-muted-foreground/40")}/>;
}

export function TagChip({ tag, onRemove, disabled }: { tag: TagResponse; onRemove?: () => void; disabled?: boolean }) {
  return <span className="inline-flex max-w-full items-center gap-1.5 rounded-full border bg-background py-0.5 pr-1 pl-2 text-xs">
    <TagDot color={tag.color}/>
    <span className="truncate">{tag.path}</span>
    {onRemove && <button type="button" className="rounded-full p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground disabled:opacity-50" disabled={disabled} aria-label={`Remove tag ${tag.path}`} onClick={onRemove}><X className="size-3"/></button>}
  </span>;
}

type TagApi = typeof tagRpc;

/**
 * Tag field for a task or subtask (PLAN §6): shows its tags, suggests existing tags while
 * typing so near duplicates are reused, and creates nested tags from a path such as
 * home/garden only when nothing existing is picked. Keyboard alternative to drag-to-tag.
 */
export function TagPicker({ targetKind, targetId, label, tags, api = tagRpc, revision = 0, onChanged }: {
  targetKind: "Task" | "Subtask" | "Pad"; targetId: string; label: string; tags: TagResponse[]; api?: TagApi; revision?: number; onChanged: () => void;
}) {
  const [ids, setIds] = React.useState<string[]>([]);
  const [text, setText] = React.useState("");
  const [suggestions, setSuggestions] = React.useState<TagResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const inputId = React.useId();

  React.useEffect(() => {
    let current = true;
    void api.targetTags({ targetKind, targetId }).then(result => {
      if (!current) return;
      if (result.ok) setIds([...result.tagIds]); else setError(result.error?.message ?? "Could not load tags.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, targetKind, targetId, revision]);

  React.useEffect(() => {
    let current = true;
    const query = text.trim();
    if (!query) { setSuggestions([]); return; }
    void api.suggest({ text: query }).then(result => { if (current && result.ok) setSuggestions([...result.tags]); }).catch(() => { if (current) setSuggestions([]); });
    return () => { current = false; };
  }, [api, text]);

  async function apply(action: () => Promise<{ ok: boolean; tagIds: readonly string[]; error: { message: string } | null }>) {
    if (busy) return;
    setBusy(true); setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? "The tag could not be saved.");
      setIds([...result.tagIds]); setText(""); setSuggestions([]);
      onChanged();
    } catch (e) { setError(e instanceof Error ? e.message : "The tag could not be saved."); }
    finally { setBusy(false); }
  }

  const typed = text.trim().replace(/^#/, "");
  const exact = suggestions.find(tag => tag.path.toLocaleLowerCase() === typed.toLocaleLowerCase());
  const link = (tag?: TagResponse) => void apply(() => api.link({ operationId: operationId(), targetKind, targetId, tagId: tag?.id ?? null, path: tag ? null : typed }));
  const byId = new Map(tags.map(tag => [tag.id, tag]));
  const attached = ids.map(id => byId.get(id)).filter((tag): tag is TagResponse => tag !== undefined);

  return <div className="grid gap-2">
    <label htmlFor={inputId} className="text-xs font-medium text-muted-foreground">{label}</label>
    {attached.length > 0 && <ul className="flex flex-wrap gap-1.5" aria-label={`${label} applied`}>{attached.map(tag => <li key={tag.id} className="max-w-full">
      <TagChip tag={tag} disabled={busy} onRemove={() => void apply(() => api.unlink({ operationId: operationId(), targetKind, targetId, tagId: tag.id }))}/>
    </li>)}</ul>}
    <Input id={inputId} value={text} disabled={busy} autoComplete="off" placeholder="Add a tag, e.g. home/garden"
      onChange={e => setText(e.target.value)}
      onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); if (typed) link(exact); } }}/>
    {typed && <ul className="grid gap-0.5" aria-label="Tag suggestions">
      {suggestions.filter(tag => !ids.includes(tag.id)).map(tag => <li key={tag.id}>
        <button type="button" className="flex w-full items-center gap-2 rounded-md px-2 py-1 text-left text-sm hover:bg-accent focus-visible:bg-accent focus-visible:outline-none" disabled={busy} onClick={() => link(tag)}>
          <TagDot color={tag.color}/><span className="truncate">Add #{tag.path}</span>
        </button>
      </li>)}
      {!exact && <li><button type="button" className="flex w-full items-center gap-2 rounded-md px-2 py-1 text-left text-sm text-primary hover:bg-accent focus-visible:bg-accent focus-visible:outline-none" disabled={busy} onClick={() => link()}>
        <Plus className="size-3.5"/><span className="truncate">Create #{typed}</span>
      </button></li>}
    </ul>}
    {error && <InlineError>{error}</InlineError>}
  </div>;
}

/**
 * Sidebar tag tree (PLAN §6/§10): nested tags with colors and open-task counts; each tag is a
 * view of its own and a drop target that tags a dragged task without moving it.
 */
export function TagTree({ tags, counts, activeTagId, busy, onOpen, onTagTask }: {
  tags: TagResponse[]; counts: ReadonlyMap<string, number>; activeTagId: string | null; busy: boolean;
  onOpen: (tag: TagResponse) => void; onTagTask: (taskId: string, tag: TagResponse) => void;
}) {
  const [over, setOver] = React.useState<string | null>(null);
  if (tags.length === 0) return <p className="px-2 py-1 text-muted-foreground">No tags yet. Create one below or type one on a task.</p>;
  const accepts = (event: React.DragEvent) => !busy && Array.from(event.dataTransfer?.types ?? []).includes(taskDragType);
  return <nav aria-label="Tags"><ol className="grid gap-px">{tags.map(tag => <li key={tag.id}
    className={cn("rounded-md", over === tag.id && "bg-brand-soft ring-1 ring-primary")}
    onDragOver={event => { if (!accepts(event)) return; event.preventDefault(); event.dataTransfer.dropEffect = "copy"; setOver(tag.id); }}
    onDragLeave={() => setOver(current => current === tag.id ? null : current)}
    onDrop={event => {
      setOver(null);
      const taskId = event.dataTransfer?.getData(taskDragType);
      if (!taskId || busy) return;
      event.preventDefault();
      onTagTask(taskId, tag);
    }}>
    <Button type="button" variant="ghost" disabled={busy} aria-current={activeTagId === tag.id ? "page" : undefined}
      className="h-8 w-full min-w-0 justify-start gap-2 px-2 font-normal text-sidebar-foreground hover:bg-sidebar-accent disabled:opacity-100 aria-[current=page]:bg-brand-soft aria-[current=page]:font-semibold aria-[current=page]:text-primary"
      style={{ paddingLeft: `${0.5 + tag.depth * 0.85}rem` }} onClick={() => onOpen(tag)}>
      <TagDot color={tag.color}/><span className="truncate">{tag.name}</span>
      {(counts.get(tag.id) ?? 0) > 0 && <span className="ml-auto text-xs text-muted-foreground tabular-nums">{counts.get(tag.id)}</span>}
    </Button>
  </li>)}</ol></nav>;
}

export function NewTagForm({ busy, onCreate, autoFocus = false }: { busy: boolean; onCreate: (path: string) => Promise<boolean>; autoFocus?: boolean }) {
  const [path, setPath] = React.useState("");
  return <form className="mt-2 flex gap-2" onSubmit={event => { event.preventDefault(); void onCreate(path).then(created => { if (created) setPath(""); }); }}>
    <label className="sr-only" htmlFor="new-tag">New tag</label>
    <Input id="new-tag" autoFocus={autoFocus} className="bg-background" value={path} onChange={e => setPath(e.target.value)} placeholder="New tag, e.g. home/garden" autoComplete="off"/>
    <Button variant="outline" size="icon" disabled={busy || !path.trim()} aria-label="Create tag"><Hash/></Button>
  </form>;
}

/**
 * Rename, color, move, merge (with an affected-count preview) and delete for one tag
 * (PLAN §6). Every change goes through the tag RPC; the parent refreshes afterwards.
 */
export function TagSettings({ tag, tags, api = tagRpc, onChanged, onDeleted }: {
  tag: TagResponse; tags: TagResponse[]; api?: TagApi; onChanged: () => Promise<void>; onDeleted: () => Promise<void>;
}) {
  const [name, setName] = React.useState(tag.name);
  const [mergeTarget, setMergeTarget] = React.useState("");
  const [preview, setPreview] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  React.useEffect(() => { setName(tag.name); setMergeTarget(""); setPreview(null); setError(null); }, [tag.id, tag.version]);

  const inside = (candidate: TagResponse) => candidate.id === tag.id || candidate.path.startsWith(`${tag.path}/`);

  async function change(action: () => Promise<{ ok: boolean; error: { message: string } | null }>, after: () => Promise<void> = onChanged) {
    if (busy) return;
    setBusy(true); setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? "The tag could not be changed.");
      await after();
    } catch (e) { setError(e instanceof Error ? e.message : "The tag could not be changed."); }
    finally { setBusy(false); }
  }

  async function showPreview(targetId: string) {
    setMergeTarget(targetId); setPreview(null);
    if (!targetId) return;
    const result = await api.previewMerge({ sourceId: tag.id, targetId });
    if (!result.ok) { setError(result.error?.message ?? "Could not preview the merge."); return; }
    const target = tags.find(candidate => candidate.id === targetId);
    setPreview(`Merging into #${target?.path}: ${result.taskLinksMoved} task tag(s) move, ${result.taskLinksAlreadyPresent} already there; ${result.subtaskLinksMoved + result.subtaskLinksAlreadyPresent} subtask tag(s); ${result.padLinksMoved + result.padLinksAlreadyPresent} pad note tag(s); ${result.childTagsMoved} nested tag(s) move, ${result.childTagsMerged} combine with same-named tags. #${tag.path} is then removed.`);
  }

  const base = { operationId: operationId(), tagId: tag.id, expectedVersion: tag.version };
  return <div className="grid gap-4" aria-label={`Settings for tag ${tag.path}`}>
    <div className="grid gap-3 sm:grid-cols-2">
      <form className="flex items-end gap-2" onSubmit={event => { event.preventDefault(); void change(() => api.rename({ ...base, operationId: operationId(), name })); }}>
        <Field label="Name" className="flex-1"><Input value={name} disabled={busy} onChange={e => setName(e.target.value)}/></Field>
        <Button variant="outline" disabled={busy || !name.trim() || name === tag.name}>Rename</Button>
      </form>
      <Field label="Color"><NativeSelect value={tag.color ?? ""} disabled={busy} onChange={e => void change(() => api.setColor({ ...base, operationId: operationId(), color: e.target.value || null }))}>
        <NativeSelectOption value="">No color</NativeSelectOption>
        {tagColors.map(color => <NativeSelectOption key={color} value={color}>{color[0].toUpperCase() + color.slice(1)}</NativeSelectOption>)}
      </NativeSelect></Field>
      <Field label="Nested under"><NativeSelect value={tag.parentId ?? ""} disabled={busy} onChange={e => void change(() => api.move({ ...base, operationId: operationId(), parentId: e.target.value || null }))}>
        <NativeSelectOption value="">Top level</NativeSelectOption>
        {tags.filter(candidate => !inside(candidate)).map(candidate => <NativeSelectOption key={candidate.id} value={candidate.id}>{candidate.path}</NativeSelectOption>)}      </NativeSelect></Field>
      <Field label="Merge into"><NativeSelect value={mergeTarget} disabled={busy} onChange={e => void showPreview(e.target.value)}>
        <NativeSelectOption value="">Choose a tag…</NativeSelectOption>
        {tags.filter(candidate => !inside(candidate)).map(candidate => <NativeSelectOption key={candidate.id} value={candidate.id}>{candidate.path}</NativeSelectOption>)}
      </NativeSelect></Field>
    </div>
    {preview && <div className="grid gap-2 rounded-lg border bg-background p-3" role="status">
      <p>{preview}</p>
      <div className="flex gap-2">
        <Button type="button" disabled={busy} onClick={() => { const target = tags.find(candidate => candidate.id === mergeTarget); if (target) void change(() => api.merge({ operationId: operationId(), sourceId: tag.id, sourceVersion: tag.version, targetId: target.id, targetVersion: target.version }), onDeleted); }}>Merge tags</Button>
        <Button type="button" variant="outline" disabled={busy} onClick={() => { setMergeTarget(""); setPreview(null); }}>Cancel</Button>
      </div>
    </div>}
    <div><Button type="button" variant="outline" className="text-destructive hover:bg-destructive/10 hover:text-destructive" disabled={busy}
      onClick={() => void change(() => api.delete(base), onDeleted)}><Trash2/>Delete tag</Button>
      <p className="mt-1 text-xs text-muted-foreground">Deleting removes the tag (and nested tags) from tasks but keeps the tasks. You can restore it from Deleted tags. Deleting or merging a mapped GitHub tag also removes its label from linked issues on the next sync.</p></div>
    {error && <InlineError>{error}</InlineError>}
  </div>;
}
