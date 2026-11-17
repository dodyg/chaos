import React from "react";
import { attachmentRpc, type AttachmentDto } from "#neoastra";
import { Crop, Download, ExternalLink, Eye, EyeOff, FileText, Film, FolderOpen, ImageIcon, Music, Paperclip, Pencil, Plus, Trash2, Undo2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ImageEditor } from "./ImageEditor";

/**
 * Attachments (PLAN2 §9.1). Files cross the host boundary only as base64 chunks of at most 1 MiB
 * (`begin`, `chunk`, `commit`; `read` the other way), never as paths. Bytes shown in the page
 * become `blob:` object URLs made here, so nothing is ever fetched from the internet and the
 * Markdown only ever holds `chaos:attachment/<id>` references.
 */

export type AttachmentApi = Pick<typeof attachmentRpc, "list" | "resolve" | "begin" | "chunk" | "commit" | "cancel" | "rename" | "delete" | "restore" | "read" | "open" | "showInFolder" | "saveCopy" | "pageImage" | "editImage" | "thumbnail">;
export type AttachmentOwnerKind = "task" | "diary" | "pad" | "page";
export type AttachmentInfo = AttachmentDto;

/** Fired after a file is added, renamed or removed, so anything derived from attachments (a page background) can refresh. */
const changedEvent = "chaos:attachments-changed";
export function notifyAttachmentsChanged() {
  try { window.dispatchEvent(new Event(changedEvent)); } catch { /* no window */ }
}

/**
 * The file name a page's `background:` asks for, or null. Only `attachments/<name>` counts: one
 * file name, no folders, drive letters, web addresses or parent paths. Mirrors
 * `PageFrontMatter.AttachmentBackgroundName` in C#; both are tested against the same table.
 */
export function attachmentBackgroundName(value: string | null | undefined): string | null {
  const text = (value ?? "").trim();
  if (!/^attachments\//i.test(text)) return null;
  const name = text.slice("attachments/".length).trim();
  if (name.length === 0 || name.length > 200 || /[/\\:\u0000-\u001f]/.test(name) || name.includes("..") || /[. ]$/.test(name)) return null;
  return name;
}

/** True for a value that points at the web, which a page background never loads. */
export function isWebBackground(value: string | null | undefined): boolean {
  return /^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test((value ?? "").trim());
}

export const attachmentLinkPattern = /^chaos:attachment\/([0-9a-fA-F-]{36})$/;

/** The ID in a `chaos:attachment/<id>` link, or null. */
export function internalAttachmentId(url: string): string | null {
  return attachmentLinkPattern.exec(url)?.[1] ?? null;
}

/** The Markdown that references an attachment: an image in place, or a file link. */
export function attachmentMarkdown(item: Pick<AttachmentDto, "id" | "fileName" | "preview">): string {
  const name = item.fileName.replace(/[[\]\\]/g, "\\$&");
  return `${item.preview === "image" ? "!" : ""}[${name}](chaos:attachment/${item.id})`;
}

export function formatBytes(value: string | number): string {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(bytes < 10 * 1024 * 1024 ? 1 : 0)} MB`;
}

export function bytesToBase64(bytes: Uint8Array): string {
  let text = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) text += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return btoa(text);
}

export function base64ToBytes(value: string): Uint8Array<ArrayBuffer> {
  const text = atob(value);
  const bytes = new Uint8Array(text.length);
  for (let index = 0; index < text.length; index++) bytes[index] = text.charCodeAt(index);
  return bytes;
}

type Failure = { error: { message: string } | null };
function fail(result: Failure, fallback: string): Error {
  return new Error(result.error?.message ?? fallback);
}

/** Adds one file to an owner: begin, sequential chunks, commit. Throws an Error with a readable message. */
export async function uploadFile(api: AttachmentApi, ownerKind: AttachmentOwnerKind, ownerId: string, file: File, onProgress?: (fraction: number) => void): Promise<AttachmentDto> {
  const begun = await api.begin({ ownerKind, ownerId, fileName: file.name || "pasted file", size: String(file.size) });
  if (!begun.ok) throw fail(begun, "The file could not be added.");
  try {
    let index = 0;
    for (let offset = 0; offset < file.size; offset += begun.chunkBytes) {
      const slice = new Uint8Array(await file.slice(offset, Math.min(file.size, offset + begun.chunkBytes)).arrayBuffer());
      const sent = await api.chunk({ uploadId: begun.uploadId, index: index++, base64: bytesToBase64(slice) });
      if (!sent.ok) throw fail(sent, "The file could not be added.");
      onProgress?.(Math.min(1, (offset + slice.length) / file.size));
    }
    const done = await api.commit({ operationId: crypto.randomUUID(), uploadId: begun.uploadId });
    if (!done.ok || !done.attachment) throw fail(done, "The file could not be added.");
    return done.attachment;
  } catch (error) {
    await api.cancel({ uploadId: begun.uploadId }).catch(() => undefined);
    throw error;
  }
}

const shown = new Set(["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "video/mp4", "video/webm", "video/quicktime", "audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav"]);
const cache = new Map<string, Promise<string>>();
const cacheLimit = 40;

/** A `blob:` URL for an attachment's bytes, built from chunked reads and cached by content. Only previewable media types are built. */
export function attachmentUrl(api: AttachmentApi, item: AttachmentDto): Promise<string> {
  if (!shown.has(item.mediaType)) return Promise.reject(new Error("This kind of file is not previewed."));
  const key = `${item.sha256}:${item.mediaType}`;
  const known = cache.get(key);
  if (known) return known;
  const made = (async () => {
    const parts: Uint8Array<ArrayBuffer>[] = [];
    const total = Number(item.byteSize);
    for (let offset = 0; offset < total;) {
      const slice = await api.read({ attachmentId: item.id, offset: String(offset), length: 1024 * 1024 });
      if (!slice.ok) throw fail(slice, "This file is not on this computer.");
      const bytes = base64ToBytes(slice.base64);
      if (bytes.length === 0) break;
      parts.push(bytes);
      offset += bytes.length;
    }
    return URL.createObjectURL(new Blob(parts, { type: item.mediaType }));
  })();
  cache.set(key, made);
  made.catch(() => cache.delete(key));
  while (cache.size > cacheLimit) {
    const oldest = cache.keys().next().value as string;
    const url = cache.get(oldest);
    cache.delete(oldest);
    void url?.then(value => URL.revokeObjectURL(value)).catch(() => undefined);
  }
  return made;
}

/** Images this big show a small JPEG inline (made by the host once, kept as a local file); the full image opens on click. */
export const thumbnailThresholdBytes = 2 * 1024 * 1024;
const thumbnails = new Map<string, Promise<string>>();

export function attachmentThumbnailUrl(api: AttachmentApi, item: AttachmentDto): Promise<string> {
  const key = `${item.sha256}:thumb`;
  const known = thumbnails.get(key);
  if (known) return known;
  const made = (async () => {
    const result = await api.thumbnail({ attachmentId: item.id });
    if (!result.ok) throw fail(result, "No preview is available.");
    return URL.createObjectURL(new Blob([base64ToBytes(result.base64)], { type: "image/jpeg" }));
  })();
  thumbnails.set(key, made);
  made.catch(() => thumbnails.delete(key));
  return made;
}

/** The inline thumbnail of a large image: `{ url }` when ready, `{ url: null, failed }` when the host cannot make one. */
export function useThumbnailUrl(api: AttachmentApi, item: AttachmentDto | undefined, enabled: boolean): { url: string | null; failed: boolean } {
  const [state, setState] = React.useState<{ key: string; url: string | null; failed: boolean }>({ key: "", url: null, failed: false });
  const key = item && enabled && item.bytes === "present" ? `${item.id}:${item.sha256}` : "";
  React.useEffect(() => {
    if (!item || !key) return;
    let current = true;
    attachmentThumbnailUrl(api, item).then(url => { if (current) setState({ key, url, failed: false }); }, () => { if (current) setState({ key, url: null, failed: true }); });
    return () => { current = false; };
  }, [api, key]);
  return state.key === key ? { url: state.url, failed: state.failed } : { url: null, failed: false };
}

/** Test hook: forget every cached object URL. */
export function clearAttachmentUrls() {
  for (const url of cache.values()) void url.then(value => URL.revokeObjectURL(value)).catch(() => undefined);
  cache.clear();
  for (const url of thumbnails.values()) void url.then(value => URL.revokeObjectURL(value)).catch(() => undefined);
  thumbnails.clear();
}

export function useAttachmentUrl(api: AttachmentApi, item: AttachmentDto | undefined, enabled = true): { url: string | null; error: string | null } {
  const [state, setState] = React.useState<{ key: string; url: string | null; error: string | null }>({ key: "", url: null, error: null });
  const key = item && enabled && item.bytes === "present" ? `${item.id}:${item.sha256}` : "";
  React.useEffect(() => {
    if (!item || !key) return;
    let current = true;
    attachmentUrl(api, item).then(url => { if (current) setState({ key, url, error: null }); }, (error: unknown) => { if (current) setState({ key, url: null, error: error instanceof Error ? error.message : "Could not load the file." }); });
    return () => { current = false; };
  }, [api, key]);
  if (!item || !enabled) return { url: null, error: null };
  if (item.bytes !== "present") return { url: null, error: "Not on this computer" };
  return state.key === key ? { url: state.url, error: state.error } : { url: null, error: null };
}

/**
 * The blob URL of the image attached to a page under a given name, for page backgrounds.
 * Returns undefined while loading and `{ url: null }` when the page has no such image.
 */
export function usePageImage(api: AttachmentApi, pageId: string | null, name: string | null): { url: string | null } | undefined {
  const [state, setState] = React.useState<{ key: string; url: string | null } | null>(null);
  const [tick, setTick] = React.useState(0);
  React.useEffect(() => {
    const refresh = () => setTick(value => value + 1);
    window.addEventListener(changedEvent, refresh);
    return () => window.removeEventListener(changedEvent, refresh);
  }, []);
  const key = pageId && name ? `${pageId}|${name}|${tick}` : "";
  React.useEffect(() => {
    if (!key || !pageId || !name) return;
    let current = true;
    void (async () => {
      const found = await api.pageImage({ pageId, name }).catch(() => null);
      if (!found?.ok || !found.attachment || found.attachment.bytes !== "present") { if (current) setState({ key, url: null }); return; }
      const url = await attachmentUrl(api, found.attachment).catch(() => null);
      if (current) setState({ key, url });
    })();
    return () => { current = false; };
  }, [api, key]);
  if (!key) return { url: null };
  return state?.key === key ? { url: state.url } : undefined;
}

function KindIcon({ item }: { item: AttachmentDto }) {
  const className = "size-4 shrink-0 text-muted-foreground";
  return item.preview === "image" ? <ImageIcon aria-hidden="true" className={className}/>
    : item.preview === "video" ? <Film aria-hidden="true" className={className}/>
    : item.preview === "audio" ? <Music aria-hidden="true" className={className}/>
    : <FileText aria-hidden="true" className={className}/>;
}

/** The previewable bytes of one attachment: an image, a PDF in the WebView's viewer, a video or audio player. */
export function AttachmentBody({ api, item, className }: { api: AttachmentApi; item: AttachmentDto; className?: string }) {
  const { url, error } = useAttachmentUrl(api, item);
  if (error) return <span role="status" className="text-sm text-muted-foreground">{error === "Not on this computer" ? "This file is not on this computer yet." : error}</span>;
  if (!url) return <span role="status" className="text-sm text-muted-foreground">Loading {item.fileName}…</span>;
  switch (item.preview) {
    case "image": return <img src={url} alt={item.fileName} width={item.width ?? undefined} height={item.height ?? undefined} className={cn("h-auto max-w-full", className)}/>;
    case "pdf": return <iframe src={url} title={item.fileName} className={cn("h-[28rem] w-full rounded-md border bg-background", className)}/>;
    case "video": return <video src={url} controls preload="metadata" aria-label={item.fileName} className={cn("max-h-[28rem] w-full rounded-md", className)}/>;
    case "audio": return <audio src={url} controls preload="metadata" aria-label={item.fileName} className={cn("w-full", className)}/>;
    default: return null;
  }
}

/** Open, Show in folder and Save a copy, as the host allows them: files that can run code only offer the last two. */
export function AttachmentActions({ api, item, onMessage }: { api: AttachmentApi; item: AttachmentDto; onMessage: (message: string) => void }) {
  async function run(action: "open" | "showInFolder" | "saveCopy") {
    const result = await api[action]({ attachmentId: item.id }).catch(() => null);
    if (!result) onMessage("That did not work. Try again.");
    else if (!result.ok) onMessage(result.error?.message ?? "That did not work.");
    else if (action === "saveCopy") onMessage(`Saved a copy${result.path ? ` to ${result.path}` : ""}.`);
  }
  const present = item.bytes === "present";
  return <>
    {item.canOpen && <Button type="button" variant="ghost" size="xs" disabled={!present} onClick={() => void run("open")} title={`Open ${item.fileName} in another app`}><ExternalLink/>Open</Button>}
    <Button type="button" variant="ghost" size="xs" disabled={!present} onClick={() => void run("showInFolder")} title="Show the file in its folder"><FolderOpen/>Show in folder</Button>
    <Button type="button" variant="ghost" size="xs" disabled={!present} onClick={() => void run("saveCopy")} title="Save a copy to the export folder"><Download/>Save a copy</Button>
  </>;
}

/** A file reference in Markdown: a card with a preview where possible. */
export function AttachmentCard({ api, item, label }: { api: AttachmentApi; item: AttachmentDto | undefined; label: React.ReactNode }) {
  const [open, setOpen] = React.useState(false);
  const [message, setMessage] = React.useState("");
  if (!item) return <span className="inline-flex items-center gap-1 text-muted-foreground" title="This file was removed or is not on this computer."><FileText className="size-3.5"/><span className="line-through">{label}</span><span className="text-xs">(missing file)</span></span>;
  const previewable = item.preview === "pdf" || item.preview === "video" || item.preview === "audio" || item.preview === "image";
  return <span data-slot="attachment-card" className="my-1 grid gap-1.5 rounded-md border bg-muted/30 p-2 text-sm">
    <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
      <KindIcon item={item}/><span className="min-w-0 truncate font-medium">{label}</span><span className="text-xs text-muted-foreground">{formatBytes(item.byteSize)}</span>
      {item.bytes !== "present" && <span className="text-xs text-muted-foreground">Not on this computer</span>}
      {previewable && item.bytes === "present" && <Button type="button" variant="ghost" size="xs" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? <EyeOff/> : <Eye/>}{open ? "Hide preview" : "Preview"}</Button>}
      <AttachmentActions api={api} item={item} onMessage={setMessage}/>
    </span>
    {open && <AttachmentBody api={api} item={item}/>}
    {message && <span role="status" className="text-xs text-muted-foreground">{message}</span>}
  </span>;
}

type PanelProps = {
  api?: AttachmentApi;
  ownerKind: AttachmentOwnerKind;
  ownerId: string;
  /** Changing this reloads the list (the editor bumps it after a paste or drop). */
  reloadKey?: number;
  /** Puts the reference into the text at the caret. */
  onInsert?: (markdown: string) => void;
  onChanged?: () => void;
};

/** Lists the files attached to one task, diary entry, pad note or page, and adds, renames, previews and removes them. */
export function AttachmentPanel({ api = attachmentRpc, ownerKind, ownerId, reloadKey = 0, onInsert, onChanged }: PanelProps) {
  const [items, setItems] = React.useState<readonly AttachmentDto[] | null>(null);
  const [message, setMessage] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [previewId, setPreviewId] = React.useState<string | null>(null);
  const [renaming, setRenaming] = React.useState<{ id: string; name: string } | null>(null);
  const [removed, setRemoved] = React.useState<AttachmentDto | null>(null);
  const [dragging, setDragging] = React.useState(false);
  const [editing, setEditing] = React.useState<AttachmentDto | null>(null);
  const input = React.useRef<HTMLInputElement | null>(null);
  const load = React.useCallback(async () => {
    const result = await api.list({ ownerKind, ownerId }).catch(() => null);
    if (result?.ok) setItems(result.attachments); else if (result) setMessage(result.error?.message ?? "");
  }, [api, ownerKind, ownerId]);
  React.useEffect(() => { void load(); }, [load, reloadKey]);

  async function add(files: File[]) {
    if (files.length === 0) return;
    setBusy(true); setMessage("");
    const added: AttachmentDto[] = [];
    for (const file of files) {
      try { added.push(await uploadFile(api, ownerKind, ownerId, file)); }
      catch (error) { setMessage(`${file.name || "That file"}: ${error instanceof Error ? error.message : "could not be added."}`); }
    }
    setBusy(false);
    await load();
    onChanged?.();
    notifyAttachmentsChanged();
    if (added.length > 0) setMessage(`Added ${added.map(item => item.fileName).join(", ")}.`);
  }

  async function change(action: () => Promise<{ ok: boolean; attachment: AttachmentDto | null; error: { message: string } | null }>, after?: (item: AttachmentDto) => void) {
    const result = await action().catch(() => null);
    if (!result) setMessage("That did not work. Try again.");
    else if (!result.ok || !result.attachment) setMessage(result.error?.message ?? "That did not work.");
    else after?.(result.attachment);
    await load();
    onChanged?.();
    notifyAttachmentsChanged();
  }

  const remove = (item: AttachmentDto) => change(() => api.delete({ operationId: crypto.randomUUID(), attachmentId: item.id, expectedVersion: item.version }), gone => { setRemoved(gone); setMessage(`Removed ${gone.fileName}.`); });
  const restore = (item: AttachmentDto) => change(() => api.restore({ operationId: crypto.randomUUID(), attachmentId: item.id, expectedVersion: item.version }), back => { setRemoved(null); setMessage(`Restored ${back.fileName}.`); });
  const rename = (item: AttachmentDto, name: string) => change(() => api.rename({ operationId: crypto.randomUUID(), attachmentId: item.id, expectedVersion: item.version, fileName: name }), () => setRenaming(null));

  return <section data-slot="attachment-panel" aria-label="Attachments" className={cn("grid gap-2 rounded-md border p-2 text-sm", dragging && "border-primary bg-primary/5")}
    onDragOver={event => { if (event.dataTransfer?.types.includes("Files")) { event.preventDefault(); setDragging(true); } }}
    onDragLeave={() => setDragging(false)}
    onDrop={event => { setDragging(false); if (event.dataTransfer?.files.length) { event.preventDefault(); void add([...event.dataTransfer.files]); } }}>
    <div className="flex items-center justify-between gap-2">
      <h3 className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground"><Paperclip aria-hidden="true" className="size-3.5"/>Attachments{items && items.length > 0 ? ` (${items.length})` : ""}</h3>
      <Button type="button" variant="outline" size="xs" disabled={busy} onClick={() => input.current?.click()}><Plus/>Add files</Button>
      <input ref={input} type="file" multiple className="sr-only" aria-label="Choose files to attach" tabIndex={-1} onChange={event => { void add([...(event.target.files ?? [])]); event.target.value = ""; }}/>
    </div>
    {items && items.length === 0 && <p className="text-xs text-muted-foreground">Drop files here, paste a screenshot into the text, or choose files.</p>}
    {items && items.length > 0 && <ul className="grid gap-1.5">
      {items.map(item => <li key={item.id} className="grid gap-1.5 rounded-md bg-muted/30 p-1.5">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <KindIcon item={item}/>
          {renaming?.id === item.id
            ? <form className="flex items-center gap-1" onSubmit={event => { event.preventDefault(); void rename(item, renaming.name); }}>
                <Input aria-label={`New name for ${item.fileName}`} value={renaming.name} autoFocus className="h-7 w-48" onChange={event => setRenaming({ id: item.id, name: event.target.value })} onKeyDown={event => { if (event.key === "Escape") setRenaming(null); }}/>
                <Button type="submit" size="xs">Save</Button><Button type="button" variant="ghost" size="xs" onClick={() => setRenaming(null)}>Cancel</Button>
              </form>
            : <span className="min-w-0 truncate font-medium" title={item.fileName}>{item.fileName}</span>}
          <span className="text-xs text-muted-foreground">{formatBytes(item.byteSize)}</span>
          {item.bytes !== "present" && <span className="text-xs text-muted-foreground">Not on this computer</span>}
        </div>
        <div className="flex flex-wrap items-center gap-0.5">
          {onInsert && <Button type="button" variant="ghost" size="xs" onClick={() => onInsert(attachmentMarkdown(item))} title="Insert a reference at the cursor"><Plus/>Insert</Button>}
          {item.preview !== "none" && item.preview !== "text" && item.bytes === "present" && <Button type="button" variant="ghost" size="xs" aria-expanded={previewId === item.id} onClick={() => setPreviewId(previewId === item.id ? null : item.id)}>{previewId === item.id ? <EyeOff/> : <Eye/>}{previewId === item.id ? "Hide preview" : "Preview"}</Button>}
          <AttachmentActions api={api} item={item} onMessage={setMessage}/>
          {item.preview === "image" && item.bytes === "present" && item.width && item.height && <Button type="button" variant="ghost" size="xs" onClick={() => setEditing(item)}><Crop/>Edit image</Button>}
          <Button type="button" variant="ghost" size="xs" onClick={() => setRenaming({ id: item.id, name: item.fileName })}><Pencil/>Rename</Button>
          <Button type="button" variant="ghost" size="xs" className="text-destructive" onClick={() => void remove(item)}><Trash2/>Remove</Button>
        </div>
        {previewId === item.id && <AttachmentBody api={api} item={item}/>}
      </li>)}
    </ul>}
    {editing && <ImageEditor api={api} item={editing} onClose={() => setEditing(null)} onSaved={saved => { setMessage(`Edited ${saved.fileName}. Undo is in the edit history.`); void load(); onChanged?.(); }}/>}
    <div className="flex flex-wrap items-center gap-2" aria-live="polite">
      {busy && <span role="status" className="text-xs text-muted-foreground">Adding…</span>}
      {message && !busy && <span role="status" className="text-xs text-muted-foreground">{message}</span>}
      {removed && <Button type="button" variant="ghost" size="xs" onClick={() => void restore(removed)}><Undo2/>Undo</Button>}
    </div>
  </section>;
}
