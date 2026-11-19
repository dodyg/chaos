import React from "react";
import {
  attachmentRpc,
  padRpc,
  siteRpc,
  tagRpc,
  type pageRpc,
  type CaptureDraftDto,
  type ContainerResponse,
  type PadItemResponse,
  type TagResponse,
} from "#neoastra";
import { QuickAdd, type CapturePreviewApi } from "./QuickAdd";
import {
  Archive,
  ArchiveRestore,
  ArrowDown,
  ArrowUp,
  Camera,
  Copy,
  FileText,
  Globe,
  ListPlus,
  NotebookPen,
  PenLine,
  Pencil,
  Plus,
  RotateCcw,
  StickyNote,
  Trash2,
  X,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, DeletedItems, EmptyState, PageHeader } from "@/components/common";
import { MarkdownEditor, type LinkCandidate } from "./MarkdownEditor";
import { MarkdownPreview, type MarkdownLinks } from "./markdown";
import { TagPicker } from "./tags";
import { useDragReorder } from "./dragReorder";
import { DrawingNote, DrawingPreview } from "./PadDrawing";
import { VoiceRecorder, type RecorderEngine } from "./VoiceRecorder";
import { attachmentMarkdown, uploadFile, type AttachmentApi } from "./attachments";
import { voiceTitle } from "./voice";
import type { Region } from "./drawing";
import { DraftRegistry, type DraftSaver, type DraftSnapshot } from "./padAutosave";
import type { PageRequest } from "./Pages";

export type PadApi = typeof padRpc;

/** A request to show the pad: a note (optionally with a backlink anchor) or a new note (itemId null). */
export type PadFocus = {
  key: number;
  itemId: string | null;
  anchor?: { state: string; start: number | null; end: number | null; quote: string; region?: Region | null } | null;
};

const operationId = () => crypto.randomUUID();

/** A short readable label for an untitled note: its first line of text. */
export function padExcerpt(markdown: string, kind?: string): string {
  if (kind === "drawing") return "Drawing";
  for (const raw of markdown.split("\n")) {
    const line = raw
      .trim()
      .replace(/^(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)/, "")
      .trim();
    if (line) return line.length > 80 ? `${line.slice(0, 79)}…` : line;
  }
  return "Empty note";
}

/** The quick-add text for a selection: its first line without list or heading markers. */
export function titleFromSelection(text: string): string {
  const line = padExcerpt(text);
  return line === "Empty note" ? "" : line.replace(/…$/, "");
}

/** Stand-in for a new note that is not stored yet. */
function newPlaceholder(markdown: string): PadItemResponse {
  return {
    id: "new",
    markdown,
    version: "0",
    draftVersion: "0",
    archived: false,
    archivedAtUtc: null,
    deleted: false,
    createdAtUtc: "",
    updatedAtUtc: "",
    tagIds: [],
    tasks: [],
    kind: "text",
    previewAttachmentId: null,
    width: 0,
    height: 0,
  } as unknown as PadItemResponse;
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
  /** Photos and voice notes are stored as attachments of the new note. */
  attachmentApi?: AttachmentApi;
  /** How voice notes record; tests pass a fake. */
  recorder?: RecorderEngine;
  tagApi?: typeof tagRpc;
  /** "New site": fetches a page and has a local model summarize and tag it. */
  siteApi?: typeof siteRpc;
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
  /** Pages: a note can become a page, or be added to a day's page. */
  pageApi?: typeof pageRpc;
  today?: string | null;
  onOpenPage?: (request: PageRequest) => void;
};

/**
 * The Personal scratch pad (PLAN §9, §15 WP5): untitled Markdown notes that save as you
 * type, ordered by hand, tagged and archived; "Make task" on a selection with a backlink;
 * and a weekly review that suggests archiving old notes.
 */
export function ScratchPad({
  api = padRpc,
  attachmentApi = attachmentRpc,
  recorder,
  tagApi = tagRpc,
  siteApi = siteRpc,
  captureApi,
  registry,
  containers,
  tags,
  revision,
  focus,
  links,
  searchLinks,
  onChanged,
  onOpenTask,
  onError,
  pageApi,
  today,
  onOpenPage,
}: Props) {
  const [archived, setArchived] = React.useState(false);
  const [tagFilter, setTagFilter] = React.useState("");
  const [items, setItems] = React.useState<PadItemResponse[]>([]);
  const [deleted, setDeleted] = React.useState<PadItemResponse[]>([]);
  const [loaded, setLoaded] = React.useState(false);
  const [openId, setOpenId] = React.useState<string | null>(null);
  const [expandedIds, setExpandedIds] = React.useState<ReadonlySet<string>>(new Set());
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [missingQuote, setMissingQuote] = React.useState<string | null>(null);
  const [selectRange, setSelectRange] = React.useState<{ start: number; end: number; key: number } | null>(null);
  const [selection, setSelection] = React.useState<{ start: number; end: number }>({ start: 0, end: 0 });
  const [makeTask, setMakeTask] = React.useState<{ start: number; end: number; text: string } | null>(null);
  const [review, setReview] = React.useState<{ due: boolean; suggestions: PadItemResponse[] } | null>(null);
  const [reviewChoice, setReviewChoice] = React.useState<ReadonlySet<string> | null>(null);
  const [newKey, setNewKey] = React.useState(0);
  /** The "New site" address form; `reading` while the page is fetched and the local model works. */
  const [siteUrl, setSiteUrl] = React.useState<string | null>(null);
  const [reading, setReading] = React.useState(false);
  /** The part of a drawing a task came from, outlined when that task opens the pad. */
  const [highlight, setHighlight] = React.useState<Region | null>(null);
  /**
   * A new note exists only here until its first character is typed, so closing it untouched
   * leaves nothing behind: no stored note, no deleted note, no undo step. `itemId` is set once
   * the first text created the stored note; the same editor keeps going without a remount.
   */
  const [newNote, setNewNote] = React.useState<{ key: number; itemId: string | null } | null>(null);
  const [draftText, setDraftText] = React.useState("");
  const draftTextRef = React.useRef("");
  const draftKeyRef = React.useRef<number | null>(null);
  draftKeyRef.current = newNote?.key ?? null;
  const creating = React.useRef(false);
  const pendingFocus = React.useRef<PadFocus | null>(null);
  const drag = useDragReorder();

  const open = items.find((item) => item.id === openId) ?? null;
  // A clean saver is dropped when the stored text changed elsewhere (undo), so the memo also
  // follows the loaded text; a saver with unsaved text is kept by the registry regardless.
  const saver = React.useMemo(
    () => (open && open.kind !== "drawing" ? saverFor(registry, api, open) : null),
    [open?.id, open?.markdown, open?.kind, registry, api],
  );
  const snapshot = useSnapshot(saver);

  // A site note is written in the background: while one still says so, check back until it is done.
  const pendingSite = items.some((item) => item.markdown.includes("Summarizing in the background"));
  React.useEffect(() => {
    if (!pendingSite) return;
    const timer = setInterval(() => {
      void load()
        .then(() => onChanged())
        .catch(() => undefined);
    }, 4000);
    return () => clearInterval(timer);
  }, [pendingSite, archived, tagFilter]);

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
      if (existing.snapshot.text !== item.markdown)
        registry.forget(item.id); // changed elsewhere (undo): reload from the store
      else existing.updateVersion(item.version);
    }
    setItems([...list.items]);
    if (removed.ok) setDeleted([...removed.items]);
    if (status.ok) setReview({ due: status.due, suggestions: [...status.suggestions] });
    setLoaded(true);
    const wanted = pendingFocus.current;
    if (wanted?.itemId && list.items.some((item) => item.id === wanted.itemId)) {
      pendingFocus.current = null;
      applyFocus(wanted);
    }
  }
  const loadRef = React.useRef(load);
  loadRef.current = load;
  React.useEffect(() => {
    void loadRef.current().catch((e: unknown) => onError(e instanceof Error ? e.message : String(e)));
  }, [archived, tagFilter, revision]);

  function applyFocus(request: PadFocus) {
    setOpenId(request.itemId);
    setMakeTask(null);
    const anchor = request.anchor;
    setHighlight(anchor?.region ?? null);
    if (anchor?.region) {
      setMissingQuote(null);
      return;
    }
    if (anchor && anchor.state !== "Missing" && anchor.start !== null && anchor.end !== null) {
      setMissingQuote(null);
      setSelectRange({ start: anchor.start, end: anchor.end, key: request.key });
    } else {
      setMissingQuote(anchor ? anchor.quote : null);
    }
  }

  React.useEffect(() => {
    if (!focus) return;
    if (focus.itemId === null) {
      createNote();
      return;
    }
    const itemId = focus.itemId;
    void (async () => {
      const result = await api.get({ itemId });
      const item = result.ok ? result.items[0] : undefined;
      if (!item) {
        onError(result.error?.message ?? "That pad note is not available.");
        return;
      }
      if (item.deleted) {
        onError("That pad note is deleted. Restore it from Deleted notes to open it.");
        setArchived(false);
        return;
      }
      if (items.some((row) => row.id === itemId) && item.archived === archived) {
        applyFocus(focus);
        return;
      }
      pendingFocus.current = focus;
      setTagFilter("");
      if (item.archived !== archived) setArchived(item.archived);
      else await load();
    })().catch((e: unknown) => onError(e instanceof Error ? e.message : String(e)));
  }, [focus?.key]);

  async function run<T extends { ok: boolean; error: { message: string; fields?: readonly { message: string }[] } | null }>(
    action: () => Promise<T>,
    failure: string,
    after?: (result: T) => void,
  ) {
    if (busy) return;
    setBusy(true);
    onError(null);
    setNotice(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.fields?.map((field) => field.message).join("; ") || result.error?.message || failure);
      after?.(result);
      await load();
      onChanged();
    } catch (e) {
      onError(e instanceof Error ? e.message : failure);
    } finally {
      setBusy(false);
    }
  }

  /** Structural changes need the note's latest row version: unsaved text goes first. */
  async function settled(item: PadItemResponse): Promise<string | null> {
    const current = registry.get(item.id);
    if (!current) return item.version;
    if (!(await current.flush())) {
      onError("Save the note's text first: the latest change could not be saved.");
      return null;
    }
    return current.snapshot.version;
  }

  function createNote() {
    if (saver) void saver.flush();
    setArchived(false);
    setTagFilter("");
    setOpenId(null);
    setMakeTask(null);
    setMissingQuote(null);
    setSelection({ start: 0, end: 0 });
    draftTextRef.current = "";
    setDraftText("");
    creating.current = false;
    setNewNote((current) => ({ key: (current?.key ?? 0) + 1, itemId: null }));
    setNewKey((key) => key + 1);
  }

  /** A drawing is stored empty straight away: there is no first character to wait for. */
  async function createDrawing() {
    if (saver) void saver.flush();
    setNewNote(null);
    setMakeTask(null);
    setMissingQuote(null);
    setHighlight(null);
    setArchived(false);
    setTagFilter("");
    await run(
      () => api.createDrawing({ operationId: operationId(), width: 1600, height: 1200 }),
      "Could not add the drawing.",
      (result) => {
        const created = result.items[0];
        if (created) setOpenId(created.id);
      },
    );
  }

  /**
   * A note that starts from a file: a photo (the image at the top) or a voice note (a first line
   * and the audio). The note is made, the file attached, then the note's text set; if the file
   * cannot be stored the empty note is deleted again, so a failure leaves nothing half made.
   */
  async function createMediaNote(file: File, title: string) {
    if (saver) void saver.flush();
    setNewNote(null);
    setMakeTask(null);
    setMissingQuote(null);
    setHighlight(null);
    setArchived(false);
    setTagFilter("");
    const created = await api.create({ operationId: operationId(), markdown: "" });
    const note = created.ok ? created.items[0] : undefined;
    if (!note)
      throw new Error(
        created.error?.fields.map((field) => field.message).join("; ") || created.error?.message || "Could not add the note.",
      );
    try {
      const attachment = await uploadFile(attachmentApi, "pad", note.id, file);
      const markdown = `${
        title
          ? `${title}

`
          : ""
      }${attachmentMarkdown(attachment)}
`;
      const saved = await api.save({ itemId: note.id, clientRevision: "1", markdown });
      if (!saved.ok) throw new Error(saved.error?.message ?? "The note could not be saved.");
    } catch (error) {
      await api.delete({ operationId: operationId(), itemId: note.id, expectedVersion: note.version }).catch(() => undefined);
      await load().catch(() => undefined);
      throw error;
    }
    registry.forget(note.id);
    await load();
    setOpenId(note.id);
    onChanged();
  }

  /** Store the address as a note at once; the summary and tags arrive later from the background reader. */
  async function addSite() {
    const url = (siteUrl ?? "").trim();
    if (!url || reading) return;
    if (saver) void saver.flush();
    setReading(true);
    onError(null);
    setNotice(null);
    try {
      const result = await siteApi.add({ operationId: operationId(), url });
      if (!result.ok || !result.item)
        throw new Error(
          result.error?.fields?.map((field) => field.message).join("; ") || result.error?.message || "The site could not be added.",
        );
      setNewNote(null);
      setMakeTask(null);
      setMissingQuote(null);
      setHighlight(null);
      setArchived(false);
      setTagFilter("");
      setSiteUrl(null);
      await load();
      setNotice("Site added. The summary is being written in the background and appears in the note when it is ready.");
      onChanged();
    } catch (e) {
      onError(e instanceof Error ? e.message : "The site could not be added.");
    } finally {
      setReading(false);
    }
  }

  const photoInput = React.useRef<HTMLInputElement | null>(null);
  const [photoOpen, setPhotoOpen] = React.useState(false);
  async function addPhoto(file: File | undefined) {
    if (!file) return;
    try {
      onError(null);
      setPhotoOpen(false);
      await createMediaNote(file, "");
    } catch (e) {
      onError(e instanceof Error ? e.message : "The photo could not be added.");
    }
  }
  /** Ctrl+V in the photo panel: take the first image on the clipboard. */
  function pasteImage(event: React.ClipboardEvent) {
    const file = Array.from(event.clipboardData.files).find((f) => f.type.startsWith("image/"));
    if (!file) return;
    event.preventDefault();
    void addPhoto(file);
  }
  /** The "Paste from clipboard" button, for when the keyboard shortcut is not at hand. */
  async function pasteImageFromClipboard() {
    try {
      for (const clip of await navigator.clipboard.read()) {
        const type = clip.types.find((t) => t.startsWith("image/"));
        if (!type) continue;
        const blob = await clip.getType(type);
        await addPhoto(new File([blob], `pasted-image.${type.split("/")[1] || "png"}`, { type }));
        return;
      }
      onError("There is no image on the clipboard.");
    } catch {
      onError("The clipboard could not be read. Click the box below and press Ctrl+V instead.");
    }
  }

  /** The first typed text stores the note; later text goes through that note's autosave. */
  function editDraft(value: string) {
    draftTextRef.current = value;
    setDraftText(value);
    if (!newNote || newNote.itemId !== null || creating.current || !value.trim()) return;
    creating.current = true;
    const key = newNote.key;
    void (async () => {
      try {
        const result = await api.create({ operationId: operationId(), markdown: value });
        const created = result.ok ? result.items[0] : undefined;
        if (!created)
          throw new Error(
            result.error?.fields.map((field) => field.message).join("; ") || result.error?.message || "Could not add the note.",
          );
        const createdSaver = saverFor(registry, api, created);
        if (draftTextRef.current !== created.markdown) createdSaver.edit(draftTextRef.current);
        await load();
        if (draftKeyRef.current === key) {
          setNewNote({ key, itemId: created.id });
          setOpenId(created.id);
        }
        onChanged();
      } catch (e) {
        creating.current = false;
        onError(e instanceof Error ? e.message : "Could not add the note.");
      }
    })();
  }

  /** Closes the new note; untouched, it simply disappears. */
  function closeDraft() {
    if (newNote?.itemId) {
      void registry.get(newNote.itemId)?.flush();
      setOpenId(null);
    }
    setNewNote(null);
    setMakeTask(null);
  }

  async function select(item: PadItemResponse) {
    if (saver && saver.itemId !== item.id) void saver.flush(); // a failure keeps the draft in the registry and shows it
    setNewNote(null);
    setOpenId(item.id);
    setMakeTask(null);
    setMissingQuote(null);
    setSelection({ start: 0, end: 0 });
  }

  async function changeItems(kind: "archive" | "unarchive", item: PadItemResponse) {
    const version = await settled(item);
    if (version === null) return;
    await run(
      () => api[kind]({ operationId: operationId(), items: [{ itemId: item.id, expectedVersion: version }] }),
      `Could not ${kind} the note.`,
      () => {
        if (openId === item.id) setOpenId(null);
      },
    );
  }

  async function remove(item: PadItemResponse) {
    if (!window.confirm("Delete this note? You can restore it from Deleted notes.")) return;
    const version = await settled(item);
    if (version === null) return;
    await run(
      () => api.delete({ operationId: operationId(), itemId: item.id, expectedVersion: version }),
      "Could not delete the note.",
      () => {
        if (openId === item.id) setOpenId(null);
        registry.forget(item.id);
      },
    );
  }

  async function reorder(item: PadItemResponse, insertionIndex: number) {
    const version = await settled(item);
    if (version === null) return;
    await run(
      () => api.reorder({ operationId: operationId(), itemId: item.id, expectedVersion: version, insertionIndex }),
      "Could not move the note.",
    );
  }

  /** Turns the note into a page (its first line names it) or adds it to today's page; one undoable change either way. */
  async function toPage(item: PadItemResponse, where: "page" | "today") {
    if (!pageApi || !onOpenPage) return;
    const version = await settled(item);
    if (version === null) return;
    if (where === "page") {
      await run(
        () =>
          pageApi.fromPad({ operationId: operationId(), padItemId: item.id, expectedVersion: version, parentPageId: null, title: null }),
        "Could not move the note to a page.",
        (result) => {
          setOpenId(null);
          registry.forget(item.id);
          const page = result.pages[0];
          if (page) onOpenPage({ kind: "page", pageId: page.id });
        },
      );
    } else if (today) {
      await run(
        () => pageApi.appendPad({ operationId: operationId(), padItemId: item.id, expectedVersion: version, date: today }),
        "Could not add the note to today's page.",
        () => {
          setOpenId(null);
          setNotice("Added the note to today's page and archived it here.");
        },
      );
    }
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
      operationId: operation,
      padItemId: open.id,
      sourceRevision: String(current.revision),
      start: form.start,
      end: form.end,
      quote: current.text.slice(form.start, form.end),
      title: draft.title,
      notes: null,
      containerId: draft.containerId,
      plannedDate: draft.plannedDate,
      dueDate: draft.dueDate,
      tagIds: [],
      draft,
    });
    if (!result.ok)
      return result.error?.fields.map((field) => field.message).join("; ") || result.error?.message || "Could not make the task.";
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
    await run(
      () => api.completeReview({ operationId: operationId(), archive }),
      "Could not finish the review.",
      () => {
        setReviewChoice(null);
        setNotice(
          archive.length === 0
            ? "Review done. Nothing was archived."
            : `Review done. Archived ${archive.length} note${archive.length === 1 ? "" : "s"}.`,
        );
      },
    );
  }

  async function copyDraft(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      setNotice("Copied the unsaved text to the clipboard.");
    } catch {
      onError("Could not copy the text. Select it in the editor and copy it by hand.");
    }
  }

  /** One note card; the new note reuses it under a stable key so its editor survives being stored. */
  function noteCard(item: PadItemResponse, index: number, options: { key: string; isNew: boolean }) {
    const isNew = options.isNew;
    const placeholder = isNew && newNote?.itemId === null;
    const isOpen = isNew || item.id === openId;
    const draft = registry.get(item.id)?.snapshot;
    const text = draft?.text ?? item.markdown;
    const isDrawing = item.kind === "drawing";
    const label = padExcerpt(text, item.kind);
    return (
      <li
        key={options.key}
        className={cn("grid gap-2 rounded-xl border bg-card p-3 shadow-xs", isOpen && "border-primary ring-1 ring-primary")}
        {...(!archived && !isNew
          ? drag.target(
              ordered.map((row) => row.id),
              index,
              "pad",
              busy || isOpen,
              (id, insertionIndex) => {
                const moving = ordered.find((row) => row.id === id);
                if (moving) void reorder(moving, insertionIndex);
              },
            )
          : {})}
      >
        <div className="flex items-start gap-2">
          {!archived && !isOpen && !isNew && drag.handle(item.id, "pad", busy)}
          {placeholder ? (
            <div className="grid min-w-0 flex-1 gap-2">
              <MarkdownEditor
                key={`new-${newNote?.key}`}
                id="pad-new"
                label="Note"
                value={draftText}
                onChange={editDraft}
                links={links}
                searchLinks={searchLinks}
                rows={8}
                autoFocus
                placeholder="Write anything. Markdown works here."
                areaClassName="min-h-48"
              />
              <p className="text-xs text-muted-foreground" role="status">
                New note. It is kept once you type; close it untouched and it is gone.
              </p>
            </div>
          ) : isOpen && isDrawing ? (
            <div className="grid min-w-0 flex-1 gap-2">
              <DrawingNote
                key={item.id}
                api={api}
                attachmentApi={attachmentApi}
                item={item}
                containers={containers}
                captureApi={captureApi}
                highlight={highlight}
                onSaved={() => {
                  void load();
                }}
                onError={onError}
                onTaskMade={(title) => {
                  setNotice(`Made task “${title}”. It links back to this drawing.`);
                  void load();
                  onChanged();
                }}
              />
            </div>
          ) : isOpen && snapshot && saver ? (
            <div className="grid min-w-0 flex-1 gap-2">
              {missingQuote !== null && (
                <Banner tone="warning" onDismiss={() => setMissingQuote(null)}>
                  The text this task came from was changed or removed. It said: “{missingQuote}”
                </Banner>
              )}
              <MarkdownEditor
                key={isNew ? `new-${newNote?.key}` : `${item.id}-${newKey}`}
                id={isNew ? "pad-new" : `pad-${item.id}`}
                label="Note"
                attachmentOwner={isNew && newNote?.itemId === null ? undefined : { kind: "pad", id: item.id }}
                value={snapshot.text}
                onChange={(value) => saver.edit(value)}
                onBlur={() => void saver.flush()}
                onSelectionChange={(start, end) => setSelection({ start, end })}
                selectRange={selectRange}
                links={links}
                searchLinks={searchLinks}
                rows={8}
                autoFocus
                placeholder="Write anything. Markdown works here."
                areaClassName="min-h-48"
              />
              <SaveStatusLine snapshot={snapshot} onRetry={() => void saver.flush()} onCopy={() => void copyDraft(snapshot.text)} />
            </div>
          ) : (
            <div className="min-w-0 flex-1 px-1 text-left">
              {isDrawing ? (
                <button
                  type="button"
                  className="block w-full rounded-md text-left outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
                  aria-label={`Open note: ${label}`}
                  onClick={() => void select(item)}
                >
                  <DrawingPreview attachmentApi={attachmentApi} attachmentId={item.previewAttachmentId} label="Drawing note preview" />
                </button>
              ) : text.trim() ? (
                <>
                  <MarkdownPreview
                    source={text}
                    links={links}
                    compact
                    className={cn("text-sm", !expandedIds.has(item.id) && "max-h-40 overflow-hidden")}
                  />
                  {(text.length > 200 || text.includes("\n")) && (
                    <button
                      type="button"
                      className="mt-1 text-xs font-medium text-primary underline-offset-2 hover:underline focus-visible:underline"
                      aria-expanded={expandedIds.has(item.id)}
                      onClick={() =>
                        setExpandedIds((prev) => {
                          const next = new Set(prev);
                          if (!next.delete(item.id)) next.add(item.id);
                          return next;
                        })
                      }
                    >
                      {expandedIds.has(item.id) ? "Show less" : "Show more"}
                    </button>
                  )}
                </>
              ) : (
                <span className="text-muted-foreground italic">Empty note</span>
              )}
              {draft?.status === "failed" && (
                <span className="mt-1 block text-xs text-destructive">Not saved — edit the note to retry.</span>
              )}
            </div>
          )}
          <div className="flex shrink-0 gap-0.5">
            {!placeholder && !isOpen && !isDrawing && (
              <Button
                variant="ghost"
                size="icon-sm"
                className="text-muted-foreground"
                disabled={busy}
                aria-label={`Open note: ${label}`}
                title="Edit"
                onClick={() => void select(item)}
              >
                <Pencil />
              </Button>
            )}
            {!archived && !isNew && (
              <>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  className="text-muted-foreground"
                  disabled={busy || index === 0}
                  aria-label={`Move note ${label} up`}
                  title="Move up"
                  onClick={() => void reorder(item, index - 1)}
                >
                  <ArrowUp />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  className="text-muted-foreground"
                  disabled={busy || index === ordered.length - 1}
                  aria-label={`Move note ${label} down`}
                  title="Move down"
                  onClick={() => void reorder(item, index + 1)}
                >
                  <ArrowDown />
                </Button>
              </>
            )}
            {!placeholder && (
              <Button
                variant="ghost"
                size="icon-sm"
                className="text-muted-foreground"
                disabled={busy}
                aria-label={`${item.archived ? "Unarchive" : "Archive"} note ${label}`}
                title={item.archived ? "Unarchive" : "Archive"}
                onClick={() => void changeItems(item.archived ? "unarchive" : "archive", item)}
              >
                {item.archived ? <ArchiveRestore /> : <Archive />}
              </Button>
            )}
            {!placeholder && (
              <Button
                variant="ghost"
                size="icon-sm"
                className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                disabled={busy}
                aria-label={`Delete note ${label}`}
                title="Delete"
                onClick={() => void remove(item)}
              >
                <Trash2 />
              </Button>
            )}
            {isOpen && (
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label="Close note"
                title="Close"
                onClick={() => {
                  if (isNew) closeDraft();
                  else {
                    void saver?.flush();
                    setOpenId(null);
                    setMakeTask(null);
                  }
                }}
              >
                <X />
              </Button>
            )}
          </div>
        </div>

        {item.tasks.length > 0 && (
          <div className="flex flex-wrap items-center gap-1.5 pl-1 text-xs" aria-label="Tasks made from this note">
            <span className="text-muted-foreground">Tasks:</span>
            {item.tasks.map((task) => (
              <Button
                key={task.taskId}
                type="button"
                variant="outline"
                size="xs"
                disabled={task.deleted}
                className={cn(task.deleted && "line-through")}
                title={task.deleted ? "This task is deleted" : task.region ? "From part of this drawing" : `From “${task.quote}”`}
                onClick={() => onOpenTask(task.taskId)}
              >
                {task.title}
              </Button>
            ))}
          </div>
        )}

        {isOpen && !placeholder && isDrawing && (
          <div className="grid gap-3 border-t pt-3">
            <TagPicker
              targetKind="Pad"
              targetId={item.id}
              label="Tags"
              tags={tags}
              api={tagApi}
              revision={revision}
              onChanged={onChanged}
            />
          </div>
        )}

        {isOpen && !placeholder && !isDrawing && (
          <div className="grid gap-3 border-t pt-3">
            <div className="flex flex-wrap items-center gap-2">
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={busy || selection.end <= selection.start}
                title="Select text in the note first"
                onClick={startMakeTask}
              >
                <ListPlus />
                Make task
              </Button>
              {selection.end <= selection.start && (
                <span className="text-xs text-muted-foreground">Select a line or a few lines to make a task from them.</span>
              )}
              {pageApi && onOpenPage && (
                <span className="ml-auto flex flex-wrap gap-1">
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    disabled={busy || !(registry.get(item.id)?.snapshot.text ?? item.markdown).trim()}
                    title="Make a page from this note; its first line becomes the title"
                    onClick={() => void toPage(item, "page")}
                  >
                    <FileText />
                    Move to a page
                  </Button>
                  {today && (
                    <Button
                      type="button"
                      size="sm"
                      variant="ghost"
                      disabled={busy || !(registry.get(item.id)?.snapshot.text ?? item.markdown).trim()}
                      title="Add this note to the end of today's page and archive it"
                      onClick={() => void toPage(item, "today")}
                    >
                      <NotebookPen />
                      Add to today's page
                    </Button>
                  )}
                </span>
              )}
            </div>
            {makeTask && (
              <div className="grid gap-2 rounded-lg border p-3">
                <blockquote className="border-l-2 pl-3 text-xs whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">
                  {(snapshot?.text ?? "").slice(makeTask.start, makeTask.end)}
                </blockquote>
                <QuickAdd
                  key={`${makeTask.start}-${makeTask.end}`}
                  api={captureApi}
                  containers={containers}
                  defaultContainerId=""
                  label="Make task from selection"
                  initialText={makeTask.text}
                  allowBatch={false}
                  submitLabel="Create task"
                  autoFocus
                  onSubmit={submitMakeTask}
                  onCancel={() => setMakeTask(null)}
                />
                <p className="text-xs text-muted-foreground">
                  Edit the text to change what quick add reads. The note stays as it is; the task links back to this spot.
                </p>
              </div>
            )}
            <TagPicker
              targetKind="Pad"
              targetId={item.id}
              label="Tags"
              tags={tags}
              api={tagApi}
              revision={revision}
              onChanged={onChanged}
            />
          </div>
        )}
      </li>
    );
  }

  const ordered = items;
  const showReview = !archived && review?.due && review.suggestions.length > 0;
  return (
    <div className="grid gap-5">
      <PageHeader
        eyebrow="Scratch pad"
        title="Personal pad"
        subtitle={
          archived
            ? "Archived notes. Unarchive one to bring it back to the pad."
            : "Notes save as you type. Select text and choose Make task to turn it into a task."
        }
        actions={
          <>
            <label className="sr-only" htmlFor="pad-tag-filter">
              Filter notes by tag
            </label>
            <NativeSelect id="pad-tag-filter" value={tagFilter} onChange={(e) => setTagFilter(e.target.value)}>
              <NativeSelectOption value="">All tags</NativeSelectOption>
              {tags.map((tag) => (
                <NativeSelectOption key={tag.id} value={tag.id}>
                  #{tag.path}
                </NativeSelectOption>
              ))}
            </NativeSelect>
            <Button
              type="button"
              variant={archived ? "secondary" : "outline"}
              aria-pressed={archived}
              onClick={() => {
                setArchived((value) => !value);
                setOpenId(null);
                setNewNote(null);
              }}
            >
              {archived ? <ArchiveRestore /> : <Archive />}
              {archived ? "Show pad" : "Archived"}
            </Button>
            {!archived && (
              <VoiceRecorder
                engine={recorder}
                disabled={busy}
                onError={onError}
                onRecorded={(file, startedAt) => createMediaNote(file, voiceTitle(startedAt))}
                onAttachFile={(file) => createMediaNote(file, voiceTitle(new Date()))}
              />
            )}
            {!archived && (
              <>
                <Button
                  type="button"
                  variant={photoOpen ? "secondary" : "outline"}
                  disabled={busy}
                  onClick={() => setPhotoOpen((value) => !value)}
                  title="Make a note from a picture: choose a file or paste one"
                >
                  <Camera />
                  New photo note
                </Button>
                <input
                  ref={photoInput}
                  type="file"
                  accept="image/*"
                  className="sr-only"
                  tabIndex={-1}
                  aria-label="Photo file"
                  onChange={(event) => {
                    const file = event.target.files?.[0];
                    event.target.value = "";
                    void addPhoto(file);
                  }}
                />
              </>
            )}
            {!archived && (
              <Button
                type="button"
                variant={siteUrl !== null ? "secondary" : "outline"}
                disabled={busy || reading}
                onClick={() => setSiteUrl((value) => (value === null ? "" : null))}
                title="Summarize a web page with the local model"
              >
                <Globe />
                New site
              </Button>
            )}
            {!archived && (
              <Button type="button" variant="outline" disabled={busy} onClick={() => void createDrawing()} title="Draw on a blank canvas">
                <PenLine />
                New drawing
              </Button>
            )}
            {!archived && (
              <Button type="button" disabled={busy} onClick={createNote} title="New note (Ctrl+Shift+P)">
                <Plus />
                New note
              </Button>
            )}
          </>
        }
      />

      {notice && (
        <Banner tone="warning" onDismiss={() => setNotice(null)}>
          {notice}
        </Banner>
      )}

      {!archived && siteUrl !== null && (
        <form
          className="grid gap-2 rounded-xl border bg-card p-4"
          aria-label="New site"
          onSubmit={(event) => {
            event.preventDefault();
            void addSite();
          }}
        >
          <label htmlFor="pad-site-url" className="text-sm font-medium">
            Web address
          </label>
          <div className="flex flex-wrap gap-2">
            <Input
              id="pad-site-url"
              className="min-w-64 flex-1"
              type="text"
              inputMode="url"
              autoFocus
              placeholder="https://example.com/article"
              value={siteUrl}
              disabled={reading}
              onChange={(event) => setSiteUrl(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Escape" && !reading) setSiteUrl(null);
              }}
            />
            <Button type="submit" disabled={reading || !siteUrl.trim()}>
              {reading ? "Adding…" : "Add site"}
            </Button>
            <Button type="button" variant="ghost" disabled={reading} onClick={() => setSiteUrl(null)}>
              Cancel
            </Button>
          </div>
          <p className="text-xs text-muted-foreground" role="status">
            {reading
              ? "Saving the address…"
              : "The address is saved at once. A small local model (Ollama) then summarizes the page in the background, says whether it is worth reading and tags the note. If the site refuses, the note keeps the address and says so."}
          </p>
        </form>
      )}

      {!archived && photoOpen && (
        <div className="grid gap-2 rounded-xl border bg-card p-4" role="group" aria-label="New photo note">
          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={busy} onClick={() => photoInput.current?.click()}>
              <Camera />
              Choose a file
            </Button>
            <Button type="button" variant="outline" disabled={busy} onClick={() => void pasteImageFromClipboard()}>
              <Copy />
              Paste from clipboard
            </Button>
            <Button type="button" variant="ghost" onClick={() => setPhotoOpen(false)}>
              Cancel
            </Button>
          </div>
          <div
            tabIndex={0}
            ref={(el) => el?.focus()}
            role="textbox"
            aria-label="Paste an image here"
            onPaste={pasteImage}
            onKeyDown={(event) => {
              if (event.key === "Escape") setPhotoOpen(false);
            }}
            className="rounded-lg border border-dashed px-4 py-6 text-center text-sm text-muted-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/40"
          >
            Copy an image, click here and press Ctrl+V to paste it.
          </div>
        </div>
      )}

      {showReview &&
        (reviewChoice === null ? (
          <div role="status" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-muted/50 px-4 py-3 text-sm">
            <span>
              Weekly review: {review.suggestions.length} note{review.suggestions.length === 1 ? " has" : "s have"} not changed for a week.
              Archive the ones you are done with?
            </span>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setReviewChoice(new Set(review.suggestions.map((item) => item.id)))}
            >
              Review notes
            </Button>
          </div>
        ) : (
          <section aria-label="Weekly pad review" className="grid gap-2 rounded-xl border bg-muted/50 p-4 text-sm">
            <h3 className="font-semibold">Choose notes to archive</h3>
            <p className="text-xs text-muted-foreground">Archived notes leave the pad but stay searchable. Nothing is deleted.</p>
            <ul className="grid gap-1">
              {review.suggestions.map((item) => (
                <li key={item.id}>
                  <label className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      className="accent-primary"
                      checked={reviewChoice.has(item.id)}
                      onChange={(e) => {
                        const next = new Set(reviewChoice);
                        if (e.target.checked) next.add(item.id);
                        else next.delete(item.id);
                        setReviewChoice(next);
                      }}
                    />
                    <span className="truncate">{padExcerpt(item.markdown, item.kind)}</span>
                  </label>
                </li>
              ))}
            </ul>
            <div className="flex flex-wrap gap-2 pt-1">
              <Button type="button" size="sm" disabled={busy} onClick={() => void finishReview(reviewChoice)}>
                {reviewChoice.size === 0 ? "Finish without archiving" : `Archive ${reviewChoice.size} and finish`}
              </Button>
              <Button type="button" size="sm" variant="ghost" onClick={() => setReviewChoice(null)}>
                Not now
              </Button>
            </div>
          </section>
        ))}

      {loaded && ordered.length === 0 && !newNote && (
        <EmptyState title={archived ? "No archived notes." : tagFilter ? "No notes with this tag." : "The pad is empty."}>
          {archived ? "Notes you archive appear here." : "Choose New note and start typing. Nothing needs a title."}
        </EmptyState>
      )}

      <ol className="grid gap-2" aria-label={archived ? "Archived notes" : "Pad notes"}>
        {ordered.filter((item) => item.id !== newNote?.itemId).map((item, index) => noteCard(item, index, { key: item.id, isNew: false }))}
        {newNote &&
          !archived &&
          noteCard(items.find((item) => item.id === newNote.itemId) ?? newPlaceholder(draftText), ordered.length, {
            key: `new-${newNote.key}`,
            isNew: true,
          })}
      </ol>

      <DeletedItems
        noun="notes"
        items={deleted}
        name={(item) => padExcerpt(item.markdown, item.kind)}
        busy={busy}
        onRestore={(item) =>
          void run(
            () => api.restore({ operationId: operationId(), itemId: item.id, expectedVersion: item.version }),
            "Could not restore the note.",
          )
        }
      />
      <p className="text-xs text-muted-foreground">
        <StickyNote className="mr-1 inline size-3.5" />
        Ctrl+Shift+P opens a new note from anywhere in Chaos.
      </p>
    </div>
  );
}

function SaveStatusLine({ snapshot, onRetry, onCopy }: { snapshot: DraftSnapshot; onRetry: () => void; onCopy: () => void }) {
  if (snapshot.status === "failed") {
    return (
      <div role="alert" className="flex flex-wrap items-center gap-2 text-xs text-destructive">
        <span>Not saved: {snapshot.error ?? "the note could not be saved."} Your text is kept here.</span>
        <Button type="button" size="xs" variant="outline" onClick={onRetry}>
          <RotateCcw />
          Retry
        </Button>
        <Button type="button" size="xs" variant="ghost" onClick={onCopy}>
          <Copy />
          Copy text
        </Button>
      </div>
    );
  }
  const label = snapshot.status === "saving" ? "Saving…" : snapshot.status === "pending" ? "Unsaved changes" : "Saved";
  return (
    <p role="status" aria-live="polite" className="text-xs text-muted-foreground">
      {label}
    </p>
  );
}
