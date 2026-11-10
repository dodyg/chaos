import React from "react";
import { padRpc, tagRpc, type CaptureDraftDto, type ContainerResponse, type PadItemResponse, type TagResponse } from "#neoastra";
import { QuickAdd, type CapturePreviewApi } from "./QuickAdd";
import { Archive, ArchiveRestore, ArrowDown, ArrowUp, Copy, ListPlus, Plus, RotateCcw, StickyNote, Trash2, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, DeletedItems, EmptyState, PageHeader } from "@/components/common";
import { MarkdownEditor, type LinkCandidate } from "./MarkdownEditor";
import { MarkdownPreview, type MarkdownLinks } from "./markdown";
import { TagPicker } from "./tags";
import { useDragReorder } from "./dragReorder";
import { DraftRegistry, type DraftSaver, type DraftSnapshot } from "./padAutosave";

export type PadApi = typeof padRpc;

/** A request to show the pad: a note (optionally with a backlink anchor) or a new note (itemId null). */
export type PadFocus = {
  key: number;
  itemId: string | null;
  anchor?: { state: string; start: number | null; end: number | null; quote: string } | null;
};

const operationId = () => crypto.randomUUID();

/** A short readable label for an untitled note: its first line of text. */
export function padExcerpt(markdown: string): string {
  for (const raw of markdown.split("\n")) {
    const line = raw.trim().replace(/^(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)/, "").trim();
    if (line) return line.length > 80 ? `${line.slice(0, 79)}…` : line;
  }
  return "Empty note";
}

/** The quick-add text for a selection: its first line without list or heading markers. */
export function titleFromSelection(text: string): string {
  const line = padExcerpt(text);
  return line === "Empty note" ? "" : line.replace(/…$/, "");
}

/** Saver for one note, created from the stored state the first time it is opened. */
function saverFor(registry: DraftRegistry, api: PadApi, item: PadItemResponse): DraftSaver {
  return registry.getOrCreate({
    itemId: item.id,
    text: item.markdown,
    revision: Number(item.draftVersion),
    version: item.version,
    save: async (revision, text) => {
      const result = await api.save({ itemId: item.id, clientRevision: String(revision), markdown: text });
      if (!result.ok || !result.item) return { ok: false, message: result.error?.message ?? "The note could not be saved." };
      return { ok: true, draftVersion: Number(result.item.draftVersion), version: result.item.version, markdown: result.item.markdown };
    },
  });
}

function useSnapshot(saver: DraftSaver | null): DraftSnapshot | null {
  const [snapshot, setSnapshot] = React.useState(saver?.snapshot ?? null);
  React.useEffect(() => {
    setSnapshot(saver?.snapshot ?? null);
    return saver?.subscribe(setSnapshot);
  }, [saver]);
  return snapshot;
}

type Props = {
  api?: PadApi;
  tagApi?: typeof tagRpc;
  /** Quick-add preview used by Make task. */
  captureApi?: CapturePreviewApi;
  registry: DraftRegistry;
  containers: ContainerResponse[];
  tags: TagResponse[];
  /** Bumped by the app after undo and other outside changes: the pad reloads. */
  revision: number;
  focus: PadFocus | null;
  links: MarkdownLinks;
  searchLinks?: (query: string) => Promise<LinkCandidate[]>;
  onChanged: () => void;
  onOpenTask: (taskId: string) => void;
  onError: (message: string | null) => void;
};

/**
 * The Personal scratch pad (PLAN §9, §15 WP5): untitled Markdown notes that save as you
 * type, ordered by hand, tagged and archived; "Make task" on a selection with a backlink;
 * and a weekly review that suggests archiving old notes.
 */
export function ScratchPad({ api = padRpc, tagApi = tagRpc, captureApi, registry, containers, tags, revision, focus, links, searchLinks, onChanged, onOpenTask, onError }: Props) {
  const [archived, setArchived] = React.useState(false);
  const [tagFilter, setTagFilter] = React.useState("");
  const [items, setItems] = React.useState<PadItemResponse[]>([]);
  const [deleted, setDeleted] = React.useState<PadItemResponse[]>([]);
  const [loaded, setLoaded] = React.useState(false);
  const [openId, setOpenId] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [missingQuote, setMissingQuote] = React.useState<string | null>(null);
  const [selectRange, setSelectRange] = React.useState<{ start: number; end: number; key: number } | null>(null);
  const [selection, setSelection] = React.useState<{ start: number; end: number }>({ start: 0, end: 0 });
  const [makeTask, setMakeTask] = React.useState<{ start: number; end: number; text: string } | null>(null);
  const [review, setReview] = React.useState<{ due: boolean; suggestions: PadItemResponse[] } | null>(null);
  const [reviewChoice, setReviewChoice] = React.useState<ReadonlySet<string> | null>(null);
  const [newKey, setNewKey] = React.useState(0);
  const pendingFocus = React.useRef<PadFocus | null>(null);
  const drag = useDragReorder();

  const open = items.find(item => item.id === openId) ?? null;
  // A clean saver is dropped when the stored text changed elsewhere (undo), so the memo also
  // follows the loaded text; a saver with unsaved text is kept by the registry regardless.
  const saver = React.useMemo(() => open ? saverFor(registry, api, open) : null, [open?.id, open?.markdown, registry, api]);
  const snapshot = useSnapshot(saver);

  async function load() {
    const [list, removed, status] = await Promise.all([
      api.list({ archived, tagId: tagFilter || null }),
      api.listDeleted({ archived: false, tagId: null }),
      api.reviewStatus({}),
    ]);
    if (!list.ok) throw new Error(list.error?.message ?? "Could not load the pad.");
    for (const item of list.items) {
      const existing = registry.get(item.id);
      if (!existing || existing.dirty || existing.snapshot.status === "saving") continue;
      if (existing.snapshot.text !== item.markdown) registry.forget(item.id); // changed elsewhere (undo): reload from the store
      else existing.updateVersion(item.version);
    }
    setItems([...list.items]);
    if (removed.ok) setDeleted([...removed.items]);
    if (status.ok) setReview({ due: status.due, suggestions: [...status.suggestions] });
    setLoaded(true);
    const wanted = pendingFocus.current;
    if (wanted?.itemId && list.items.some(item => item.id === wanted.itemId)) {
      pendingFocus.current = null;
      applyFocus(wanted);
    }
  }
  const loadRef = React.useRef(load);
  loadRef.current = load;
  React.useEffect(() => { void loadRef.current().catch((e: unknown) => onError(e instanceof Error ? e.message : String(e))); }, [archived, tagFilter, revision]);

  function applyFocus(request: PadFocus) {
    setOpenId(request.itemId);
    setMakeTask(null);
    const anchor = request.anchor;
    if (anchor && anchor.state !== "Missing" && anchor.start !== null && anchor.end !== null) {
      setMissingQuote(null);
      setSelectRange({ start: anchor.start, end: anchor.end, key: request.key });
    } else {
      setMissingQuote(anchor ? anchor.quote : null);
    }
  }

  React.useEffect(() => {
    if (!focus) return;
    if (focus.itemId === null) { void createNote(); return; }
    const itemId = focus.itemId;
    void (async () => {
      const result = await api.get({ itemId });
      const item = result.ok ? result.items[0] : undefined;
      if (!item) { onError(result.error?.message ?? "That pad note is not available."); return; }
      if (item.deleted) { onError("That pad note is deleted. Restore it from Deleted notes to open it."); setArchived(false); return; }
      if (items.some(row => row.id === itemId) && item.archived === archived) { applyFocus(focus); return; }
      pendingFocus.current = focus;
      setTagFilter("");
      if (item.archived !== archived) setArchived(item.archived); else await load();
    })().catch((e: unknown) => onError(e instanceof Error ? e.message : String(e)));
  }, [focus?.key]);

  async function run<T extends { ok: boolean; error: { message: string; fields?: readonly { message: string }[] } | null }>(action: () => Promise<T>, failure: string, after?: (result: T) => void) {
    if (busy) return;
    setBusy(true); onError(null); setNotice(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.fields?.map(field => field.message).join("; ") || result.error?.message || failure);
      after?.(result);
      await load();
      onChanged();
    } catch (e) { onError(e instanceof Error ? e.message : failure); }
    finally { setBusy(false); }
  }

  /** Structural changes need the note's latest row version: unsaved text goes first. */
  async function settled(item: PadItemResponse): Promise<string | null> {
    const current = registry.get(item.id);
    if (!current) return item.version;
    if (!(await current.flush())) { onError("Save the note's text first: the latest change could not be saved."); return null; }
    return current.snapshot.version;
  }

  async function createNote() {
    await run(() => api.create({ operationId: operationId(), markdown: "" }), "Could not add a note.", result => {
      const created = result.items[0];
      if (created) { setArchived(false); setTagFilter(""); setOpenId(created.id); setNewKey(key => key + 1); setMissingQuote(null); }
    });
  }

  async function select(item: PadItemResponse) {
    if (saver && saver.itemId !== item.id) void saver.flush(); // a failure keeps the draft in the registry and shows it
    setOpenId(item.id); setMakeTask(null); setMissingQuote(null); setSelection({ start: 0, end: 0 });
  }

  async function changeItems(kind: "archive" | "unarchive", item: PadItemResponse) {
    const version = await settled(item);
    if (version === null) return;
    await run(() => api[kind]({ operationId: operationId(), items: [{ itemId: item.id, expectedVersion: version }] }), `Could not ${kind} the note.`, () => { if (openId === item.id) setOpenId(null); });
  }

  async function remove(item: PadItemResponse) {
    if (!window.confirm("Delete this note? You can restore it from Deleted notes.")) return;
    const version = await settled(item);
    if (version === null) return;
    await run(() => api.delete({ operationId: operationId(), itemId: item.id, expectedVersion: version }), "Could not delete the note.", () => { if (openId === item.id) setOpenId(null); registry.forget(item.id); });
  }

  async function reorder(item: PadItemResponse, insertionIndex: number) {
    const version = await settled(item);
    if (version === null) return;
    await run(() => api.reorder({ operationId: operationId(), itemId: item.id, expectedVersion: version, insertionIndex }), "Could not move the note.");
  }

  function startMakeTask() {
    if (!snapshot || selection.end <= selection.start) return;
    const text = snapshot.text.slice(selection.start, selection.end);
    setMakeTask({ start: selection.start, end: selection.end, text: titleFromSelection(text) });
  }

  /** Quick add reads the selection (PLAN §9); the task and its backlink are written in one transaction. */
  async function submitMakeTask(drafts: CaptureDraftDto[], operation: string): Promise<string | null> {
    const draft = drafts[0];
    if (!makeTask || !saver || !open || !draft) return "Select text in the note first.";
    if (!(await saver.flush())) return "The note could not be saved, so the task was not made. Retry saving first.";
    const current = saver.snapshot;
    const form = makeTask;
    const result = await api.makeTask({
      operationId: operation, padItemId: open.id, sourceRevision: String(current.revision), start: form.start, end: form.end,
      quote: current.text.slice(form.start, form.end), title: draft.title, notes: null, containerId: draft.containerId,
      plannedDate: draft.plannedDate, dueDate: draft.dueDate, tagIds: [], draft,
    });
    if (!result.ok) return result.error?.fields.map(field => field.message).join("; ") || result.error?.message || "Could not make the task.";
    setMakeTask(null);
    setNotice(`Made task “${result.task?.title ?? draft.title}”. It links back to this note.`);
    await load();
    onChanged();
    return null;
  }

  async function finishReview(chosen: ReadonlySet<string>) {
    const archive: { itemId: string; expectedVersion: string }[] = [];
    for (const item of review?.suggestions ?? []) {
      if (!chosen.has(item.id)) continue;
      const version = await settled(item);
      if (version === null) return;
      archive.push({ itemId: item.id, expectedVersion: version });
    }
    await run(() => api.completeReview({ operationId: operationId(), archive }), "Could not finish the review.", () => {
      setReviewChoice(null);
      setNotice(archive.length === 0 ? "Review done. Nothing was archived." : `Review done. Archived ${archive.length} note${archive.length === 1 ? "" : "s"}.`);
    });
  }

  async function copyDraft(text: string) {
    try { await navigator.clipboard.writeText(text); setNotice("Copied the unsaved text to the clipboard."); }
    catch { onError("Could not copy the text. Select it in the editor and copy it by hand."); }
  }

  const ordered = items;
  const showReview = !archived && review?.due && review.suggestions.length > 0;
  return <div className="grid gap-5">
    <PageHeader eyebrow="Scratch pad" title="Personal pad" subtitle={archived ? "Archived notes. Unarchive one to bring it back to the pad." : "Notes save as you type. Select text and choose Make task to turn it into a task."}
      actions={<>
        <label className="sr-only" htmlFor="pad-tag-filter">Filter notes by tag</label>
        <NativeSelect id="pad-tag-filter" value={tagFilter} onChange={e => setTagFilter(e.target.value)}>
          <NativeSelectOption value="">All tags</NativeSelectOption>
          {tags.map(tag => <NativeSelectOption key={tag.id} value={tag.id}>#{tag.path}</NativeSelectOption>)}
        </NativeSelect>
        <Button type="button" variant={archived ? "secondary" : "outline"} aria-pressed={archived} onClick={() => { setArchived(value => !value); setOpenId(null); }}>{archived ? <ArchiveRestore/> : <Archive/>}{archived ? "Show pad" : "Archived"}</Button>
        {!archived && <Button type="button" disabled={busy} onClick={() => void createNote()} title="New note (Ctrl+Shift+P)"><Plus/>New note</Button>}
      </>}/>

    {notice && <Banner tone="warning" onDismiss={() => setNotice(null)}>{notice}</Banner>}

    {showReview && (reviewChoice === null
      ? <div role="status" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-muted/50 px-4 py-3 text-sm">
          <span>Weekly review: {review.suggestions.length} note{review.suggestions.length === 1 ? " has" : "s have"} not changed for a week. Archive the ones you are done with?</span>
          <Button type="button" variant="outline" size="sm" onClick={() => setReviewChoice(new Set(review.suggestions.map(item => item.id)))}>Review notes</Button>
        </div>
      : <section aria-label="Weekly pad review" className="grid gap-2 rounded-xl border bg-muted/50 p-4 text-sm">
          <h3 className="font-semibold">Choose notes to archive</h3>
          <p className="text-xs text-muted-foreground">Archived notes leave the pad but stay searchable. Nothing is deleted.</p>
          <ul className="grid gap-1">{review.suggestions.map(item => <li key={item.id}><label className="flex items-center gap-2">
            <input type="checkbox" className="accent-primary" checked={reviewChoice.has(item.id)} onChange={e => { const next = new Set(reviewChoice); if (e.target.checked) next.add(item.id); else next.delete(item.id); setReviewChoice(next); }}/>
            <span className="truncate">{padExcerpt(item.markdown)}</span>
          </label></li>)}</ul>
          <div className="flex flex-wrap gap-2 pt-1">
            <Button type="button" size="sm" disabled={busy} onClick={() => void finishReview(reviewChoice)}>{reviewChoice.size === 0 ? "Finish without archiving" : `Archive ${reviewChoice.size} and finish`}</Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setReviewChoice(null)}>Not now</Button>
          </div>
        </section>)}

    {loaded && ordered.length === 0 && <EmptyState title={archived ? "No archived notes." : tagFilter ? "No notes with this tag." : "The pad is empty."}>{archived ? "Notes you archive appear here." : "Choose New note and start typing. Nothing needs a title."}</EmptyState>}

    <ol className="grid gap-2" aria-label={archived ? "Archived notes" : "Pad notes"}>{ordered.map((item, index) => {
      const isOpen = item.id === openId;
      const draft = registry.get(item.id)?.snapshot;
      const text = draft?.text ?? item.markdown;
      return <li key={item.id} className={cn("grid gap-2 rounded-xl border bg-card p-3 shadow-xs", isOpen && "border-primary ring-1 ring-primary")}
        {...(!archived ? drag.target(ordered.map(row => row.id), index, "pad", busy || isOpen, (id, insertionIndex) => { const moving = ordered.find(row => row.id === id); if (moving) void reorder(moving, insertionIndex); }) : {})}>
        <div className="flex items-start gap-2">
          {!archived && !isOpen && drag.handle(item.id, "pad", busy)}
          {isOpen && snapshot && saver
            ? <div className="grid min-w-0 flex-1 gap-2">
                {missingQuote !== null && <Banner tone="warning" onDismiss={() => setMissingQuote(null)}>The text this task came from was changed or removed. It said: “{missingQuote}”</Banner>}
                <MarkdownEditor key={`${item.id}-${newKey}`} id={`pad-${item.id}`} label="Note" value={snapshot.text} onChange={value => saver.edit(value)}
                  onBlur={() => void saver.flush()} onSelectionChange={(start, end) => setSelection({ start, end })} selectRange={selectRange}
                  links={links} searchLinks={searchLinks} rows={8} autoFocus placeholder="Write anything. Markdown works here." areaClassName="min-h-48"/>
                <SaveStatusLine snapshot={snapshot} onRetry={() => void saver.flush()} onCopy={() => void copyDraft(snapshot.text)}/>
              </div>
            : <button type="button" className="min-w-0 flex-1 rounded-md px-1 text-left outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50" aria-label={`Open note: ${padExcerpt(text)}`} onClick={() => void select(item)}>
                {text.trim() ? <MarkdownPreview source={text} links={links} compact className="pointer-events-none max-h-40 overflow-hidden text-sm"/> : <span className="text-muted-foreground italic">Empty note</span>}
                {draft?.status === "failed" && <span className="mt-1 block text-xs text-destructive">Not saved — open the note to retry.</span>}
              </button>}
          <div className="flex shrink-0 gap-0.5">
            {!archived && <>
              <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy || index === 0} aria-label={`Move note ${padExcerpt(text)} up`} title="Move up" onClick={() => void reorder(item, index - 1)}><ArrowUp/></Button>
              <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy || index === ordered.length - 1} aria-label={`Move note ${padExcerpt(text)} down`} title="Move down" onClick={() => void reorder(item, index + 1)}><ArrowDown/></Button>
            </>}
            <Button variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={busy} aria-label={`${item.archived ? "Unarchive" : "Archive"} note ${padExcerpt(text)}`} title={item.archived ? "Unarchive" : "Archive"} onClick={() => void changeItems(item.archived ? "unarchive" : "archive", item)}>{item.archived ? <ArchiveRestore/> : <Archive/>}</Button>
            <Button variant="ghost" size="icon-sm" className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" disabled={busy} aria-label={`Delete note ${padExcerpt(text)}`} title="Delete" onClick={() => void remove(item)}><Trash2/></Button>
            {isOpen && <Button variant="ghost" size="icon-sm" aria-label="Close note" title="Close" onClick={() => { void saver?.flush(); setOpenId(null); setMakeTask(null); }}><X/></Button>}
          </div>
        </div>

        {item.tasks.length > 0 && <div className="flex flex-wrap items-center gap-1.5 pl-1 text-xs" aria-label="Tasks made from this note">
          <span className="text-muted-foreground">Tasks:</span>
          {item.tasks.map(task => <Button key={task.taskId} type="button" variant="outline" size="xs" disabled={task.deleted} className={cn(task.deleted && "line-through")} title={task.deleted ? "This task is deleted" : `From “${task.quote}”`} onClick={() => onOpenTask(task.taskId)}>{task.title}</Button>)}
        </div>}

        {isOpen && <div className="grid gap-3 border-t pt-3">
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" size="sm" variant="outline" disabled={busy || selection.end <= selection.start} title="Select text in the note first" onClick={startMakeTask}><ListPlus/>Make task</Button>
            {selection.end <= selection.start && <span className="text-xs text-muted-foreground">Select a line or a few lines to make a task from them.</span>}
          </div>
          {makeTask && <div className="grid gap-2 rounded-lg border p-3">
            <blockquote className="border-l-2 pl-3 text-xs whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">{(snapshot?.text ?? "").slice(makeTask.start, makeTask.end)}</blockquote>
            <QuickAdd key={`${makeTask.start}-${makeTask.end}`} api={captureApi} containers={containers} defaultContainerId="" label="Make task from selection" initialText={makeTask.text}
              allowBatch={false} submitLabel="Create task" autoFocus onSubmit={submitMakeTask} onCancel={() => setMakeTask(null)}/>
            <p className="text-xs text-muted-foreground">Edit the text to change what quick add reads. The note stays as it is; the task links back to this spot.</p>
          </div>}
          <TagPicker targetKind="Pad" targetId={item.id} label="Tags" tags={tags} api={tagApi} revision={revision} onChanged={onChanged}/>
        </div>}
      </li>;
    })}</ol>

    <DeletedItems noun="notes" items={deleted} name={item => padExcerpt(item.markdown)} busy={busy}
      onRestore={item => void run(() => api.restore({ operationId: operationId(), itemId: item.id, expectedVersion: item.version }), "Could not restore the note.")}/>
    <p className="text-xs text-muted-foreground"><StickyNote className="mr-1 inline size-3.5"/>Ctrl+Shift+P opens a new note from anywhere in Chaos.</p>
  </div>;
}

function SaveStatusLine({ snapshot, onRetry, onCopy }: { snapshot: DraftSnapshot; onRetry: () => void; onCopy: () => void }) {
  if (snapshot.status === "failed") {
    return <div role="alert" className="flex flex-wrap items-center gap-2 text-xs text-destructive">
      <span>Not saved: {snapshot.error ?? "the note could not be saved."} Your text is kept here.</span>
      <Button type="button" size="xs" variant="outline" onClick={onRetry}><RotateCcw/>Retry</Button>
      <Button type="button" size="xs" variant="ghost" onClick={onCopy}><Copy/>Copy text</Button>
    </div>;
  }
  const label = snapshot.status === "saving" ? "Saving…" : snapshot.status === "pending" ? "Unsaved changes" : "Saved";
  return <p role="status" aria-live="polite" className="text-xs text-muted-foreground">{label}</p>;
}
