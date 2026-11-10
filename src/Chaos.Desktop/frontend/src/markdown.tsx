import React from "react";
import { unified } from "unified";
import remarkParse from "remark-parse";
import remarkGfm from "remark-gfm";
import { common, createLowlight } from "lowlight";
import TurndownService from "turndown";
import { gfm } from "turndown-plugin-gfm";
import type { Root, RootContent, PhrasingContent, ListItem, TableCell } from "mdast";
import type { Root as HastRoot, ElementContent } from "hast";
import { ImageOff, Link2Off } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * Markdown for notes, diary entries and (later) pad items (PLAN §9). Source stays raw GFM.
 * Rendering walks the mdast tree into React elements: there is no HTML string anywhere, raw
 * HTML in the source is shown as text, images never load (a placeholder stands in until
 * attachments arrive), and only http/https/mailto links are clickable — through the host
 * opener, after a click. Internal links are `[label](chaos:task/<id>)`: they resolve by ID,
 * so a rename keeps working and a deleted target shows a recoverable broken-link state.
 */

const parser = unified().use(remarkParse).use(remarkGfm);
const lowlight = createLowlight(common);

export function parseMarkdown(source: string): Root {
  return parser.parse(source) as Root;
}

export const internalLinkPattern = /^chaos:task\/([0-9a-fA-F-]{36})$/;

/** The ID of an internal task link, or null. */
export function internalTaskId(url: string): string | null {
  return internalLinkPattern.exec(url)?.[1] ?? null;
}

/** External URLs the host opener accepts; everything else renders as inert text. */
export function isSafeExternalUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:" || parsed.protocol === "mailto:";
  } catch { return false; }
}

/** IDs of every internal task link in a document (for one batched resolve call). */
export function internalLinkIds(source: string): string[] {
  const ids = new Set<string>();
  const visit = (node: Root | RootContent) => {
    if (node.type === "link") { const id = internalTaskId(node.url); if (id) ids.add(id.toLowerCase()); }
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
  const effective = resolved ? { ...links, targets: resolved } : links;
  if (!source.trim()) return <p className="text-muted-foreground italic">Nothing written yet.</p>;
  return <div data-slot="markdown" className={cn("markdown grid min-w-0 [overflow-wrap:anywhere]", compact ? "gap-1.5" : "gap-3", className)}>{renderBlocks(tree.children, effective)}</div>;
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
    case "html": return <pre key={key} className="rounded-md bg-muted px-2 py-1 text-xs whitespace-pre-wrap" title="HTML is shown as text">{node.value}</pre>;
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
    case "html": return <code key={key} className="rounded bg-muted px-1 font-mono text-[0.85em]" title="HTML is shown as text">{node.value}</code>;
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

  if (isSafeExternalUrl(url)) {
    return <button type="button" className={linkClass} title={url} onClick={() => links.onOpenExternal?.(url)}>{children}</button>;
  }

  return <span className="text-muted-foreground underline decoration-dotted" title="This link was blocked: only web, email and task links open.">{children}</span>;
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
