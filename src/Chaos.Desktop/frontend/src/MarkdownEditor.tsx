import React from "react";
import { Bold, Code, Eye, Heading, Italic, Link, List, ListChecks, ListOrdered, PenLine, Quote, SquareCode, Strikethrough, Table } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { MarkdownPreview, htmlToMarkdown, type MarkdownLinks } from "./markdown";
import { formatEdit, linkQueryAt, padLinkMarkdown, taskLinkMarkdown, type FormatAction, type TextEdit } from "./markdownEditing";

/** A `[[` autocomplete target: a task (default) or a pad note. */
export type LinkCandidate = { id: string; title: string; kind?: "task" | "pad" };

type Props = {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  rows?: number;
  placeholder?: string;
  disabled?: boolean;
  /** Candidates for `[[` internal links (tasks now; pad items with package 5). */
  searchLinks?: (query: string) => Promise<LinkCandidate[]>;
  links?: MarkdownLinks;
  onBlur?: () => void;
  /** Extra classes for the source area and preview box, e.g. a taller minimum height. */
  areaClassName?: string;
  /** Reports the source selection as UTF-16 offsets (the same units the store uses). */
  onSelectionChange?: (start: number, end: number) => void;
  /** Selects this range in the source when `key` changes (e.g. following a backlink). */
  selectRange?: { start: number; end: number; key: number } | null;
  autoFocus?: boolean;
};

const tools: { action: FormatAction; label: string; icon: React.ReactNode; shortcut?: string }[] = [
  { action: "heading", label: "Heading", icon: <Heading/> },
  { action: "bold", label: "Bold", icon: <Bold/>, shortcut: "Ctrl+B" },
  { action: "italic", label: "Italic", icon: <Italic/>, shortcut: "Ctrl+I" },
  { action: "strike", label: "Strikethrough", icon: <Strikethrough/> },
  { action: "link", label: "Link", icon: <Link/>, shortcut: "Ctrl+K" },
  { action: "bullets", label: "Bulleted list", icon: <List/> },
  { action: "numbers", label: "Numbered list", icon: <ListOrdered/> },
  { action: "checklist", label: "Checklist", icon: <ListChecks/> },
  { action: "quote", label: "Quote", icon: <Quote/> },
  { action: "code", label: "Inline code", icon: <Code/> },
  { action: "codeBlock", label: "Code block", icon: <SquareCode/> },
  { action: "table", label: "Table", icon: <Table/> },
];

/**
 * The one Markdown editor for task notes, subtask notes and diary entries (PLAN §9): a
 * source textarea (selection, keyboard and screen-reader behavior stay native), a toolbar
 * acting on the real selection, Ctrl+B/I/K, rich-text paste converted to Markdown, `[[` task
 * links, and a sanitized preview. Edits go through the browser's own insertText command when
 * available so Ctrl+Z inside the text keeps working.
 */
export function MarkdownEditor({ id, label, value, onChange, rows = 5, placeholder, disabled, searchLinks, links, onBlur, areaClassName, onSelectionChange, selectRange, autoFocus }: Props) {
  const area = React.useRef<HTMLTextAreaElement | null>(null);
  const pendingSelection = React.useRef<[number, number] | null>(null);
  const [mode, setMode] = React.useState<"source" | "preview">("source");
  const [linkQuery, setLinkQuery] = React.useState<{ start: number; query: string } | null>(null);
  const [candidates, setCandidates] = React.useState<LinkCandidate[]>([]);
  const [active, setActive] = React.useState(0);
  const listId = `${id}-links`;

  React.useLayoutEffect(() => {
    const selection = pendingSelection.current;
    if (selection && area.current) {
      area.current.setSelectionRange(selection[0], selection[1]);
      pendingSelection.current = null;
    }
  }, [value]);

  React.useEffect(() => {
    if (!selectRange) return;
    setMode("source");
    const element = area.current;
    if (!element) return;
    element.focus();
    element.setSelectionRange(selectRange.start, selectRange.end);
    onSelectionChange?.(selectRange.start, selectRange.end);
  }, [selectRange?.key]);

  React.useEffect(() => {
    if (!linkQuery || !searchLinks) { setCandidates([]); return; }
    let current = true;
    const timer = setTimeout(() => {
      void searchLinks(linkQuery.query).then(rows => { if (current) { setCandidates(rows.slice(0, 8)); setActive(0); } }).catch(() => { if (current) setCandidates([]); });
    }, 120);
    return () => { current = false; clearTimeout(timer); };
  }, [linkQuery?.query, linkQuery?.start, searchLinks]);

  function apply(edit: TextEdit) {
    const element = area.current;
    if (!element) return;
    element.focus();
    element.setSelectionRange(edit.start, edit.end);
    const expected = value.slice(0, edit.start) + edit.insert + value.slice(edit.end);
    let native = false;
    try { native = typeof document.execCommand === "function" && document.execCommand("insertText", false, edit.insert); } catch { native = false; }
    if (!native || element.value !== expected) onChange(expected);
    pendingSelection.current = [edit.selectionStart, edit.selectionEnd];
    if (native && element.value === expected) element.setSelectionRange(edit.selectionStart, edit.selectionEnd);
  }

  function format(action: FormatAction) {
    const element = area.current;
    if (!element || disabled) return;
    apply(formatEdit(value, element.selectionStart, element.selectionEnd, action));
  }

  function trackLinkQuery(text: string, caret: number) {
    setLinkQuery(searchLinks ? linkQueryAt(text, caret) : null);
  }

  function pick(candidate: LinkCandidate) {
    const element = area.current;
    if (!element || !linkQuery) return;
    const insert = candidate.kind === "pad" ? padLinkMarkdown(candidate.title, candidate.id) : taskLinkMarkdown(candidate.title, candidate.id);
    apply({ start: linkQuery.start, end: element.selectionStart, insert, selectionStart: linkQuery.start + insert.length, selectionEnd: linkQuery.start + insert.length });
    setLinkQuery(null);
  }

  function keyDown(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (linkQuery && candidates.length > 0) {
      if (event.key === "ArrowDown") { event.preventDefault(); setActive(index => (index + 1) % candidates.length); return; }
      if (event.key === "ArrowUp") { event.preventDefault(); setActive(index => (index - 1 + candidates.length) % candidates.length); return; }
      if (event.key === "Enter" || event.key === "Tab") { event.preventDefault(); pick(candidates[active]); return; }
    }
    if (event.key === "Escape" && linkQuery) { event.preventDefault(); event.stopPropagation(); setLinkQuery(null); return; }
    if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey) {
      const action = ({ b: "bold", i: "italic", k: "link" } as Record<string, FormatAction>)[event.key.toLowerCase()];
      if (action) { event.preventDefault(); format(action); }
    }
  }

  function paste(event: React.ClipboardEvent<HTMLTextAreaElement>) {
    const html = event.clipboardData?.getData("text/html");
    if (!html) return; // plain text pastes natively
    const markdown = htmlToMarkdown(html);
    if (!markdown) return;
    event.preventDefault();
    const element = event.currentTarget;
    const start = element.selectionStart;
    apply({ start, end: element.selectionEnd, insert: markdown, selectionStart: start + markdown.length, selectionEnd: start + markdown.length });
  }

  const showList = mode === "source" && linkQuery !== null && candidates.length > 0;
  return <div className="grid min-w-0 gap-1.5" data-slot="markdown-editor">
    <div className="flex items-center justify-between gap-2">
      <label htmlFor={id} className="text-xs font-medium text-muted-foreground">{label}</label>
      <div className="flex rounded-md border p-0.5" role="group" aria-label={`${label} view`}>
        <Button type="button" variant={mode === "source" ? "secondary" : "ghost"} size="xs" aria-pressed={mode === "source"} onClick={() => setMode("source")}><PenLine/>Write</Button>
        <Button type="button" variant={mode === "preview" ? "secondary" : "ghost"} size="xs" aria-pressed={mode === "preview"} onClick={() => { setLinkQuery(null); setMode("preview"); }}><Eye/>Preview</Button>
      </div>
    </div>
    {mode === "source" ? <>
      <div role="toolbar" aria-label={`${label} formatting`} aria-controls={id} className="flex flex-wrap gap-0.5">
        {tools.map(tool => <Button key={tool.action} type="button" variant="ghost" size="icon-xs" className="text-muted-foreground" disabled={disabled}
          aria-label={tool.label} title={tool.shortcut ? `${tool.label} (${tool.shortcut})` : tool.label}
          onMouseDown={event => event.preventDefault()} onClick={() => format(tool.action)}>{tool.icon}</Button>)}
      </div>
      <div className="relative">
        <Textarea ref={area} id={id} rows={rows} value={value} disabled={disabled} placeholder={placeholder} spellCheck
          className={cn("font-mono text-sm leading-relaxed", areaClassName)} dir="auto"
          role={searchLinks ? "combobox" : undefined} aria-expanded={searchLinks ? showList : undefined} aria-controls={showList ? listId : undefined}
          aria-autocomplete={searchLinks ? "list" : undefined} aria-activedescendant={showList ? `${listId}-${active}` : undefined}
          onChange={event => { onChange(event.target.value); trackLinkQuery(event.target.value, event.target.selectionStart); }}
          onSelect={event => { trackLinkQuery(event.currentTarget.value, event.currentTarget.selectionStart); onSelectionChange?.(event.currentTarget.selectionStart, event.currentTarget.selectionEnd); }}
          autoFocus={autoFocus}
          onKeyDown={keyDown} onPaste={paste} onBlur={() => { setTimeout(() => setLinkQuery(null), 150); onBlur?.(); }}/>
        {showList && <ul id={listId} role="listbox" aria-label="Link to a task or note" className="absolute inset-x-0 top-full z-20 mt-1 grid max-h-56 overflow-y-auto rounded-md border bg-popover p-1 text-sm shadow-md">
          {candidates.map((candidate, index) => <li key={candidate.id} id={`${listId}-${index}`} role="option" aria-selected={index === active}
            className={cn("cursor-pointer truncate rounded-sm px-2 py-1.5", index === active && "bg-accent text-accent-foreground")}
            onMouseDown={event => { event.preventDefault(); pick(candidate); }}>{candidate.kind === "pad" && <span className="mr-1.5 text-xs text-muted-foreground">Note</span>}{candidate.title}</li>)}
        </ul>}
      </div>
      <p className="text-[0.7rem] text-muted-foreground">Markdown. Type <kbd className="font-mono">[[</kbd> to link a task or note.</p>
    </> : <div className={cn("min-h-24 rounded-md border bg-background px-3 py-2 text-sm", areaClassName)}><MarkdownPreview source={value} links={links}/></div>}
  </div>;
}
