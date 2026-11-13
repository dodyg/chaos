import React from "react";
import { Dialog } from "radix-ui";
import { unified } from "unified";
import remarkParse from "remark-parse";
import remarkGfm from "remark-gfm";
import { common, createLowlight } from "lowlight";
import TurndownService from "turndown";
import { gfm } from "turndown-plugin-gfm";
import type { Root, RootContent, PhrasingContent, ListItem, TableCell } from "mdast";
import type { Root as HastRoot, ElementContent } from "hast";
import { ImageOff, Link2Off, X } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * Markdown for notes, diary entries and (later) pad items (PLAN §9). Source stays raw GFM.
 * Rendering walks the mdast tree into React elements: there is no HTML string anywhere, raw
 * HTML in the source is shown as text except for isolated image tags with web URLs. Markdown
 * images remain placeholders, and only http/https/mailto links are clickable — through the host
 * opener, after a click. Internal links are `[label](chaos:task/<id>)`: they resolve by ID,
 * so a rename keeps working and a deleted target shows a recoverable broken-link state.
 */

const parser = unified().use(remarkParse).use(remarkGfm);
const lowlight = createLowlight(common);

export function parseMarkdown(source: string): Root {
  return parser.parse(source) as Root;
}

export const internalLinkPattern = /^chaos:task\/([0-9a-fA-F-]{36})$/;
export const padLinkPattern = /^chaos:pad\/([0-9a-fA-F-]{36})$/;

/** The ID of an internal task link, or null. */
export function internalTaskId(url: string): string | null {
  return internalLinkPattern.exec(url)?.[1] ?? null;
}

/** The ID of an internal pad-note link, or null. */
export function internalPadId(url: string): string | null {
  return padLinkPattern.exec(url)?.[1] ?? null;
}

/** External URLs the host opener accepts; everything else renders as inert text. */
export function isSafeExternalUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:" || parsed.protocol === "mailto:";
  } catch { return false; }
}

/** IDs of every internal task link in a document (for one batched resolve call). */
export function internalLinkIds(source: string, read: (url: string) => string | null = internalTaskId): string[] {
  const ids = new Set<string>();
  const visit = (node: Root | RootContent) => {
    if (node.type === "link") { const id = read(node.url); if (id) ids.add(id.toLowerCase()); }
    if ("children" in node) for (const child of node.children) visit(child as RootContent);
  };
  visit(parseMarkdown(source));
  return [...ids];
}

/** A checklist item found in a document, keyed by its text and occurrence so reconversion is recognized. */
export type ChecklistEntry = { itemKey: string; title: string; done: boolean };

export function plainText(node: Root | RootContent): string {
  if (node.type === "text" || node.type === "inlineCode") return node.value;
  if (node.type === "break") return " ";
  if ("children" in node) return (node.children as RootContent[]).map(plainText).join("");
  return "";
}

/** Checklist items in document order (PLAN §9 checklist conversion parses the AST, not the text). */
export function extractChecklist(source: string): ChecklistEntry[] {
  const found: ChecklistEntry[] = [];
  const seen = new Map<string, number>();
  const visit = (node: Root | RootContent) => {
    if (node.type === "listItem" && typeof node.checked === "boolean") {
      const first = node.children.find(child => child.type === "paragraph");
      const title = (first ? plainText(first) : "").replace(/\s+/g, " ").trim();
      if (title) {
        const occurrence = seen.get(title) ?? 0;
        seen.set(title, occurrence + 1);
        found.push({ itemKey: `${title}#${occurrence}`, title, done: node.checked });
      }
    }
    if ("children" in node) for (const child of node.children) visit(child as RootContent);
  };
  visit(parseMarkdown(source));
  return found;
}

const turndown = new TurndownService({ headingStyle: "atx", codeBlockStyle: "fenced", bulletListMarker: "-", emDelimiter: "*" });
turndown.use(gfm);
turndown.remove(["script", "style", "iframe", "object", "embed", "noscript", "template"]);

/** Rich-text paste (PLAN §9): the clipboard's HTML becomes GFM; nothing from it is ever rendered as HTML. */
export function htmlToMarkdown(html: string): string {
  return turndown.turndown(html).trim();
}

export type LinkTarget = { id: string; title: string; deleted: boolean };

export type MarkdownLinks = {
  /** Resolved internal link targets, keyed by lowercase task ID. Missing means unknown. */
  targets?: ReadonlyMap<string, LinkTarget>;
  /** Looks up internal link targets by ID; the preview calls it for the IDs in its source. */
  resolve?: (taskIds: string[]) => Promise<LinkTarget[]>;
  onOpenTask?: (taskId: string) => void;
  onOpenExternal?: (url: string) => void;
  loadGitHubImage?: (url: string) => Promise<{ dataUrl?: string; issueUrl?: string; error?: string }>;
  /** Resolved pad-note link targets, keyed by lowercase note ID. */
  padTargets?: ReadonlyMap<string, LinkTarget>;
  resolvePads?: (itemIds: string[]) => Promise<LinkTarget[]>;
  onOpenPad?: (itemId: string) => void;
};

/** Renders raw Markdown safely. `compact` tightens spacing for timeline entries. */
export function MarkdownPreview({ source, links = {}, compact = false, className }: { source: string; links?: MarkdownLinks; compact?: boolean; className?: string }) {
  const tree = React.useMemo(() => parseMarkdown(source), [source]);
  const ids = React.useMemo(() => internalLinkIds(source).sort().join(","), [source]);
  const [resolved, setResolved] = React.useState<ReadonlyMap<string, LinkTarget> | undefined>(undefined);
  const resolve = links.resolve;
  React.useEffect(() => {
    if (!resolve || !ids) { setResolved(undefined); return; }
    let current = true;
    void resolve(ids.split(",")).then(targets => { if (current) setResolved(new Map(targets.map(target => [target.id.toLowerCase(), target]))); }).catch(() => { if (current) setResolved(undefined); });
    return () => { current = false; };
  }, [ids, resolve]);
  const padIds = React.useMemo(() => internalLinkIds(source, internalPadId).sort().join(","), [source]);
  const [resolvedPads, setResolvedPads] = React.useState<ReadonlyMap<string, LinkTarget> | undefined>(undefined);
  const resolvePads = links.resolvePads;
  React.useEffect(() => {
    if (!resolvePads || !padIds) { setResolvedPads(undefined); return; }
    let current = true;
    void resolvePads(padIds.split(",")).then(targets => { if (current) setResolvedPads(new Map(targets.map(target => [target.id.toLowerCase(), target]))); }).catch(() => { if (current) setResolvedPads(undefined); });
    return () => { current = false; };
  }, [padIds, resolvePads]);
  const effective = { ...links, ...(resolved ? { targets: resolved } : {}), ...(resolvedPads ? { padTargets: resolvedPads } : {}) };
  if (!source.trim()) return <p className="text-muted-foreground italic">Nothing written yet.</p>;
  return <div data-slot="markdown" className={cn("markdown grid min-w-0 [overflow-wrap:anywhere]", compact ? "gap-1.5" : "gap-3", className)}>{renderBlocks(tree.children, effective)}</div>;
}


/** Extract only an isolated image element; never insert source HTML into the document. */
function htmlImage(source: string, links: MarkdownLinks, key: number): React.ReactNode | null {
  if (!/^\s*<img\b[\s\S]*>\s*$/i.test(source)) return null;
  const template = document.createElement("template");
  template.innerHTML = source.trim();
  const nodes = template.content.childNodes;
  if (nodes.length !== 1 || !(nodes[0] instanceof HTMLElement) || nodes[0].tagName !== "IMG") return null;
  const image = nodes[0];
  const src = image.getAttribute("src") ?? "";
  try {
    const url = new URL(src);
    if (url.protocol !== "https:" && url.protocol !== "http:") return null;
  } catch { return null; }
  const dimension = (name: string) => {
    const value = image.getAttribute(name);
    return value && /^[1-9]\d{0,3}$/.test(value) ? Number(value) : undefined;
  };
  return <PreviewImage key={key} src={src} alt={image.getAttribute("alt") ?? ""} width={dimension("width")} height={dimension("height")} links={links}/>;
}

function PreviewImage({ src, alt, width, height, links }: { src: string; alt: string; width?: number; height?: number; links: MarkdownLinks }) {
  const [image, setImage] = React.useState(src);
  const [failed, setFailed] = React.useState<{ error: string; issueUrl?: string } | null>(null);
  const [loading, setLoading] = React.useState(false);
  React.useEffect(() => { setImage(src); setFailed(null); setLoading(false); }, [src]);
  const active = React.useRef(0);
  React.useEffect(() => () => { active.current++; }, [src]);
  async function onError() {
    if (image === src && links.loadGitHubImage && /^https:\/\/github\.com\/user-attachments\/assets\/[0-9a-f-]{36}$/i.test(src)) {
      const request = ++active.current;
      setLoading(true);
      try {
        const result = await links.loadGitHubImage(src);
        if (request !== active.current) return;
        if (result.dataUrl) { setImage(result.dataUrl); setLoading(false); return; }
        setFailed({ error: result.error ?? "Could not load this GitHub attachment.", issueUrl: result.issueUrl });
      } catch { if (request === active.current) setFailed({ error: "Could not load this GitHub attachment." }); }
      if (request === active.current) setLoading(false);
    } else setFailed({ error: "Could not load this image." });
  }
  if (failed) return <span className="grid gap-1 text-sm text-muted-foreground" role="status">{failed.error}{failed.issueUrl && <button type="button" className="w-fit text-primary underline" onClick={() => links.onOpenExternal?.(failed.issueUrl!)}>Open issue on GitHub</button>}</span>;
  if (loading) return <span className="text-sm text-muted-foreground" role="status">Loading image…</span>;
  return <Dialog.Root>
    <Dialog.Trigger asChild><button type="button" className="inline-block max-w-full cursor-zoom-in rounded-sm text-left focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50" aria-label={`Enlarge ${alt || "image"}`}>
      <img src={image} alt={alt} width={width} height={height} loading="lazy" referrerPolicy="no-referrer" className="h-auto max-w-full" onError={() => void onError()}/>
    </button></Dialog.Trigger>
    <Dialog.Portal>
      <Dialog.Overlay className="fixed inset-0 z-50 bg-black/80"/>
      <Dialog.Content aria-describedby={undefined} className="fixed top-1/2 left-1/2 z-50 flex h-[min(94vh,1000px)] w-[min(96vw,1400px)] -translate-x-1/2 -translate-y-1/2 flex-col rounded-lg border bg-background p-3 shadow-xl focus:outline-none">
        <div className="flex shrink-0 items-center justify-between gap-3 pb-2"><Dialog.Title className="truncate text-sm font-medium">{alt || "Image"}</Dialog.Title><Dialog.Close asChild><button type="button" className="rounded-md p-2 text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50" aria-label="Close image"><X className="size-5"/></button></Dialog.Close></div>
        <div className="flex min-h-0 flex-1 overflow-auto"><img src={image} alt={alt} referrerPolicy="no-referrer" className="m-auto h-auto max-w-none shrink-0"/></div>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>;
}

function renderBlocks(nodes: RootContent[], links: MarkdownLinks): React.ReactNode[] {
  return nodes.map((node, index) => renderBlock(node, links, index));
}

function renderBlock(node: RootContent, links: MarkdownLinks, key: number): React.ReactNode {
  switch (node.type) {
    case "paragraph": return <p key={key}>{renderInline(node.children, links)}</p>;
    case "heading": {
      const Tag = `h${Math.min(node.depth + 2, 6)}` as "h3";
      const size = node.depth === 1 ? "text-lg" : node.depth === 2 ? "text-base" : "text-sm";
      return <Tag key={key} className={cn("font-semibold", size)}>{renderInline(node.children, links)}</Tag>;
    }
    case "blockquote": return <blockquote key={key} className="grid gap-2 border-l-2 border-input pl-3 text-muted-foreground">{renderBlocks(node.children, links)}</blockquote>;
    case "list": {
      const items = node.children.map((item, index) => renderListItem(item, links, index));
      return node.ordered
        ? <ol key={key} start={node.start ?? undefined} className="grid list-decimal gap-1 pl-5">{items}</ol>
        : <ul key={key} className={cn("grid gap-1 pl-5", node.children.some(item => typeof item.checked === "boolean") ? "list-none pl-1" : "list-disc")}>{items}</ul>;
    }
    case "code": return <CodeBlock key={key} code={node.value} lang={node.lang ?? null}/>;
    case "thematicBreak": return <hr key={key} className="border-border"/>;
    case "table": {
      const align = node.align ?? [];
      const [head, ...body] = node.children;
      const cell = (value: TableCell, column: number, header: boolean) => {
        const Cell = header ? "th" : "td";
        return <Cell key={column} style={{ textAlign: align[column] ?? undefined }} className={cn("border px-2 py-1", header && "bg-muted font-semibold")}>{renderInline(value.children, links)}</Cell>;
      };
      return <div key={key} className="overflow-x-auto"><table className="w-full border-collapse text-sm">
        {head && <thead><tr>{head.children.map((value, column) => cell(value, column, true))}</tr></thead>}
        <tbody>{body.map((row, index) => <tr key={index}>{row.children.map((value, column) => cell(value, column, false))}</tr>)}</tbody>
      </table></div>;
    }
    case "html": return htmlImage(node.value, links, key) ?? <pre key={key} className="rounded-md bg-muted px-2 py-1 text-xs whitespace-pre-wrap" title="HTML is shown as text">{node.value}</pre>;
    case "definition": return null;
    case "footnoteDefinition": return <div key={key} className="text-xs text-muted-foreground">[{node.identifier}] {renderBlocks(node.children, links)}</div>;
    default: return "children" in node ? <div key={key}>{renderInline(node.children as PhrasingContent[], links)}</div> : null;
  }
}

function renderListItem(item: ListItem, links: MarkdownLinks, key: number): React.ReactNode {
  const content = item.children.map((child, index) => child.type === "paragraph" && item.children.length === 1
    ? <React.Fragment key={index}>{renderInline(child.children, links)}</React.Fragment>
    : renderBlock(child, links, index));
  if (typeof item.checked !== "boolean") return <li key={key}>{content}</li>;
  return <li key={key} className="flex items-start gap-2">
    <input type="checkbox" checked={item.checked} disabled readOnly aria-label={item.checked ? "Done" : "Not done"} className="mt-1 size-3.5 accent-primary"/>
    <div className={cn("grid min-w-0 gap-1", item.checked && "text-muted-foreground line-through")}>{content}</div>
  </li>;
}

function renderInline(nodes: PhrasingContent[], links: MarkdownLinks): React.ReactNode[] {
  return nodes.map((node, index) => renderPhrasing(node, links, index));
}

function renderPhrasing(node: PhrasingContent, links: MarkdownLinks, key: number): React.ReactNode {
  switch (node.type) {
    case "text": return <React.Fragment key={key}>{node.value}</React.Fragment>;
    case "strong": return <strong key={key}>{renderInline(node.children, links)}</strong>;
    case "emphasis": return <em key={key}>{renderInline(node.children, links)}</em>;
    case "delete": return <del key={key}>{renderInline(node.children, links)}</del>;
    case "inlineCode": return <code key={key} className="rounded bg-muted px-1 py-0.5 font-mono text-[0.85em]">{node.value}</code>;
    case "break": return <br key={key}/>;
    case "html": return htmlImage(node.value, links, key) ?? <code key={key} className="rounded bg-muted px-1 font-mono text-[0.85em]" title="HTML is shown as text">{node.value}</code>;
    case "image": case "imageReference": {
      const alt = node.alt || "image";
      return <span key={key} role="img" aria-label={`Image not loaded: ${alt}`} className="inline-flex items-center gap-1 rounded border border-dashed px-1.5 py-0.5 text-xs text-muted-foreground">
        <ImageOff className="size-3.5"/>{alt} · images arrive with attachments
      </span>;
    }
    case "link": return <MarkdownLink key={key} url={node.url} links={links}>{renderInline(node.children, links)}</MarkdownLink>;
    case "linkReference": return <React.Fragment key={key}>{renderInline(node.children, links)}</React.Fragment>;
    case "footnoteReference": return <sup key={key}>[{node.identifier}]</sup>;
    default: return null;
  }
}

function MarkdownLink({ url, links, children }: { url: string; links: MarkdownLinks; children: React.ReactNode }) {
  const taskId = internalTaskId(url);
  const linkClass = "inline cursor-pointer rounded-sm text-left text-primary underline underline-offset-2 hover:text-primary/80 focus-visible:ring-[3px] focus-visible:ring-ring/50 outline-none";
  if (taskId) {
    const target = links.targets?.get(taskId.toLowerCase());
    if (links.targets && (!target || target.deleted)) {
      return <span className="inline-flex items-center gap-1 text-muted-foreground" title={target ? "This task is deleted. Restore it from Deleted tasks to follow the link." : "This task no longer exists on this computer."}>
        <Link2Off className="size-3.5"/><span className="line-through">{target?.title ?? children}</span><span className="text-xs">({target ? "deleted task" : "missing task"})</span>
      </span>;
    }
    return <button type="button" className={linkClass} title={target ? `Open “${target.title}”` : "Open task"} onClick={() => links.onOpenTask?.(taskId)}>{target?.title ?? children}</button>;
  }

  const padId = internalPadId(url);
  if (padId) {
    const target = links.padTargets?.get(padId.toLowerCase());
    if (links.padTargets && (!target || target.deleted)) {
      return <span className="inline-flex items-center gap-1 text-muted-foreground" title={target ? "This pad note is deleted. Restore it from the pad's deleted notes to follow the link." : "This pad note no longer exists on this computer."}>
        <Link2Off className="size-3.5"/><span className="line-through">{children}</span><span className="text-xs">({target ? "deleted note" : "missing note"})</span>
      </span>;
    }
    return <button type="button" className={linkClass} title={target ? `Open pad note “${target.title}”` : "Open pad note"} onClick={() => links.onOpenPad?.(padId)}>{children}</button>;
  }

  if (isSafeExternalUrl(url)) {
    return <button type="button" className={linkClass} title={url} onClick={() => links.onOpenExternal?.(url)}>{children}</button>;
  }

  return <span className="text-muted-foreground underline decoration-dotted" title="This link was blocked: only web, email, task and pad-note links open.">{children}</span>;
}

function CodeBlock({ code, lang }: { code: string; lang: string | null }) {
  const tree = React.useMemo<HastRoot | null>(() => {
    if (!lang || !lowlight.registered(lang)) return null;
    try { return lowlight.highlight(lang, code); } catch { return null; }
  }, [code, lang]);
  return <pre className="overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-xs leading-relaxed" data-language={lang ?? undefined}>
    <code>{tree ? tree.children.map((node, index) => renderHast(node as ElementContent, index)) : code}</code>
  </pre>;
}

/** Highlighted code as React spans; only class names from the highlighter are kept. */
function renderHast(node: ElementContent, key: number): React.ReactNode {
  if (node.type === "text") return <React.Fragment key={key}>{node.value}</React.Fragment>;
  if (node.type === "element") {
    const classes = Array.isArray(node.properties?.className) ? node.properties.className.filter((name): name is string => typeof name === "string" && name.startsWith("hljs-")) : [];
    return <span key={key} className={classes.join(" ") || undefined}>{node.children.map((child, index) => renderHast(child as ElementContent, index))}</span>;
  }
  return null;
}
