import React from "react";
import { createPortal } from "react-dom";
import {
  Bold,
  Code,
  Eye,
  Heading,
  Italic,
  Link,
  List,
  ListChecks,
  ListOrdered,
  Maximize2,
  Minimize2,
  PenLine,
  Quote,
  SquareCode,
  Strikethrough,
  Table,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { MarkdownPreview, htmlToMarkdown, type MarkdownLinks } from "./markdown";
import { attachmentRpc, peopleRpc } from "#neoastra";
import { AttachmentPanel, attachmentMarkdown, uploadFile, type AttachmentApi, type AttachmentOwnerKind } from "./attachments";
import {
  formatEdit,
  linkQueryAt,
  mentionQueryAt,
  memberLinkMarkdown,
  listLinkMarkdown,
  padLinkMarkdown,
  pageLinkMarkdown,
  taskLinkMarkdown,
  type FormatAction,
  type TextEdit,
} from "./markdownEditing";
import { continueList, indentItem, moveItem, outdentItem } from "./outline";

/** A `[[` autocomplete target: a task (default), a pad note, a page, a list or project, or a page to create. */
export type LinkCandidate = {
  id: string;
  title: string;
  kind?: "task" | "pad" | "page" | "list" | "newPage" | "member";
  icon?: string | null;
  detail?: string;
};

const candidateLabels: Record<string, string> = { pad: "Note", page: "Page", list: "List", newPage: "New page", member: "Member" };

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
  members?: readonly { id: string; name: string }[];
  spaceId?: string | null;
  links?: MarkdownLinks;
  onBlur?: () => void;
  /** Extra classes for the source area and preview box, e.g. a taller minimum height. */
  areaClassName?: string;
  /** Reports the source selection as UTF-16 offsets (the same units the store uses). */
  onSelectionChange?: (start: number, end: number) => void;
  /** Selects this range in the source when `key` changes (e.g. following a backlink). */
  selectRange?: { start: number; end: number; key: number } | null;
  autoFocus?: boolean;
  /** Pages: Tab/Shift+Tab, Alt+Up/Down and Enter work on list items as an outline. */
  outline?: boolean;
  /** Replaces the hint under the editor. */
  hint?: React.ReactNode;
  /** Text area rows grow with the text instead of scrolling (pages). */
  grow?: boolean;
  /** What files added here belong to. Without it the editor has no attachments (the item does not exist yet). */
  attachmentOwner?: { kind: AttachmentOwnerKind; id: string };
  /** Where attachments are added and read; tests pass a fake, the app uses the host. */
  attachmentApi?: AttachmentApi;
};

const tools: { action: FormatAction; label: string; icon: React.ReactNode; shortcut?: string }[] = [
  { action: "heading", label: "Heading", icon: <Heading /> },
  { action: "bold", label: "Bold", icon: <Bold />, shortcut: "Ctrl+B" },
  { action: "italic", label: "Italic", icon: <Italic />, shortcut: "Ctrl+I" },
  { action: "strike", label: "Strikethrough", icon: <Strikethrough /> },
  { action: "link", label: "Link", icon: <Link />, shortcut: "Ctrl+K" },
  { action: "bullets", label: "Bulleted list", icon: <List /> },
  { action: "numbers", label: "Numbered list", icon: <ListOrdered /> },
  { action: "checklist", label: "Checklist", icon: <ListChecks /> },
  { action: "quote", label: "Quote", icon: <Quote /> },
  { action: "code", label: "Inline code", icon: <Code /> },
  { action: "codeBlock", label: "Code block", icon: <SquareCode /> },
  { action: "table", label: "Table", icon: <Table /> },
];

/**
 * The one Markdown editor for task notes, subtask notes and diary entries (PLAN §9): a
 * source textarea (selection, keyboard and screen-reader behavior stay native), a toolbar
 * acting on the real selection, Ctrl+B/I/K, rich-text paste converted to Markdown, `[[` task
 * links, and a sanitized preview. Edits go through the browser's own insertText command when
 * available so Ctrl+Z inside the text keeps working.
 */
export function MarkdownEditor({
  id,
  label,
  value,
  onChange,
  rows = 5,
  placeholder,
  disabled,
  searchLinks,
  members,
  spaceId,
  links,
  onBlur,
  areaClassName,
  onSelectionChange,
  selectRange,
  autoFocus,
  outline,
  hint,
  grow,
  attachmentOwner,
  attachmentApi,
}: Props) {
  const area = React.useRef<HTMLTextAreaElement | null>(null);
  const pendingSelection = React.useRef<[number, number] | null>(null);
  const [mode, setMode] = React.useState<"source" | "preview">("source");
  const [spaceMembers, setSpaceMembers] = React.useState<readonly { id: string; name: string }[]>([]);
  React.useEffect(() => {
    let active = true;
    setSpaceMembers([]);
    if (spaceId && !members)
      void peopleRpc
        .list({ spaceId })
        .then((result) => {
          if (active && result.ok)
            setSpaceMembers(
              result.people
                .filter((p) => p.role !== null && !p.former && p.actorId.startsWith("person:"))
                .map((p) => ({ id: p.actorId.slice(7), name: p.displayName })),
            );
        })
        .catch(() => {
          if (active) setSpaceMembers([]);
        });
    return () => {
      active = false;
    };
  }, [spaceId, members]);
  const mentionMembers = members ?? spaceMembers;
  const [linkQuery, setLinkQuery] = React.useState<{ start: number; query: string; mention?: boolean } | null>(null);
  const [candidates, setCandidates] = React.useState<LinkCandidate[]>([]);
  const [active, setActive] = React.useState(0);
  const [expanded, setExpanded] = React.useState(false);
  const listId = `${id}-links`;
  const [attachmentKey, setAttachmentKey] = React.useState(0);
  const [attachmentNote, setAttachmentNote] = React.useState("");
  // Uploads finish after later renders; the latest closure inserts into the latest text.
  const insertReference = React.useRef<(markdown: string) => void>(() => undefined);
  const previewLinks = React.useMemo(() => (attachmentApi ? { ...links, attachmentApi } : links), [links, attachmentApi]);

  /**
   * Expanded, the editor fills the whole content area right of the sidebar. It is drawn in a
   * portal so no transformed panel (the task panel slides in) can trap it; the move remounts
   * the text area, so the caret and selection are carried over and focus returns to it.
   */
  function toggleExpanded() {
    const element = area.current;
    if (element) pendingSelection.current = [element.selectionStart, element.selectionEnd];
    setLinkQuery(null);
    setExpanded((value) => !value);
  }
  const toggled = React.useRef(false);
  React.useEffect(() => {
    if (!toggled.current) {
      toggled.current = true;
      return;
    } // not on first render
    const element = area.current;
    if (!element) return;
    const selection = pendingSelection.current;
    element.focus();
    if (selection) {
      element.setSelectionRange(selection[0], selection[1]);
      pendingSelection.current = null;
    }
  }, [expanded]);
  // Esc collapses the expanded editor (unless the link list already used it). Closing the task or note unmounts it with the portal.
  React.useEffect(() => {
    if (!expanded) return;
    function escape(event: KeyboardEvent) {
      if (event.key === "Escape" && !event.defaultPrevented) {
        event.preventDefault();
        toggleExpanded();
      }
    }
    window.addEventListener("keydown", escape);
    return () => window.removeEventListener("keydown", escape);
  }, [expanded]);

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
    if (linkQuery?.mention) {
      const query = linkQuery.query.trim().toLocaleLowerCase();
      setCandidates(
        mentionMembers
          .filter((member) => member.name.toLocaleLowerCase().includes(query))
          .slice(0, 8)
          .map((member) => ({ id: member.id, title: member.name, kind: "member" })),
      );
      setActive(0);
      return;
    }
    if (!linkQuery || !searchLinks) {
      setCandidates([]);
      return;
    }
    let current = true;
    const timer = setTimeout(() => {
      void searchLinks(linkQuery.query)
        .then((rows) => {
          if (current) {
            setCandidates(rows.slice(0, 8));
            setActive(0);
          }
        })
        .catch(() => {
          if (current) setCandidates([]);
        });
    }, 120);
    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [linkQuery?.query, linkQuery?.start, linkQuery?.mention, searchLinks, mentionMembers]);

  function apply(edit: TextEdit) {
    const element = area.current;
    if (!element) return;
    element.focus();
    element.setSelectionRange(edit.start, edit.end);
    const expected = value.slice(0, edit.start) + edit.insert + value.slice(edit.end);
    let native = false;
    try {
      native = typeof document.execCommand === "function" && document.execCommand("insertText", false, edit.insert);
    } catch {
      native = false;
    }
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
    const mention = mentionMembers.length ? mentionQueryAt(text, caret) : null;
    setLinkQuery(mention ? { ...mention, mention: true } : searchLinks ? linkQueryAt(text, caret) : null);
  }

  function pick(candidate: LinkCandidate) {
    const element = area.current;
    if (!element || !linkQuery) return;
    const insert =
      candidate.kind === "member"
        ? memberLinkMarkdown(candidate.title, candidate.id)
        : candidate.kind === "pad"
          ? padLinkMarkdown(candidate.title, candidate.id)
          : candidate.kind === "page"
            ? pageLinkMarkdown(candidate.title, candidate.id)
            : candidate.kind === "list"
              ? listLinkMarkdown(candidate.title, candidate.id)
              : candidate.kind === "newPage"
                ? `[[${candidate.title.replace(/[[\]|]/g, "")}]]`
                : taskLinkMarkdown(candidate.title, candidate.id);
    apply({
      start: linkQuery.start,
      end: element.selectionStart,
      insert,
      selectionStart: linkQuery.start + insert.length,
      selectionEnd: linkQuery.start + insert.length,
    });
    setLinkQuery(null);
  }

  function keyDown(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (linkQuery && candidates.length > 0) {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        setActive((index) => (index + 1) % candidates.length);
        return;
      }
      if (event.key === "ArrowUp") {
        event.preventDefault();
        setActive((index) => (index - 1 + candidates.length) % candidates.length);
        return;
      }
      if (event.key === "Enter" || event.key === "Tab") {
        event.preventDefault();
        pick(candidates[active]);
        return;
      }
    }
    if (event.key === "Escape" && linkQuery) {
      event.preventDefault();
      event.stopPropagation();
      setLinkQuery(null);
      return;
    }
    if (outline && !disabled) {
      const element = event.currentTarget;
      const { selectionStart, selectionEnd } = element;
      let edit: TextEdit | null = null;
      if (event.key === "Tab" && !event.ctrlKey && !event.altKey && !event.metaKey)
        edit = event.shiftKey ? outdentItem(value, selectionStart, selectionEnd) : indentItem(value, selectionStart, selectionEnd);
      else if (event.altKey && !event.ctrlKey && !event.metaKey && (event.key === "ArrowUp" || event.key === "ArrowDown"))
        edit = moveItem(value, selectionStart, selectionEnd, event.key === "ArrowUp" ? "up" : "down");
      else if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey)
        edit = continueList(value, selectionStart, selectionEnd);
      if (edit) {
        event.preventDefault();
        if (edit.start !== edit.end || edit.insert) apply(edit);
        return;
      }
      // Alt+Up/Down outside a movable item does nothing rather than moving the caret oddly.
      if (event.altKey && (event.key === "ArrowUp" || event.key === "ArrowDown")) {
        event.preventDefault();
        return;
      }
    }
    if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey) {
      const action = ({ b: "bold", i: "italic", k: "link" } as Record<string, FormatAction>)[event.key.toLowerCase()];
      if (action) {
        event.preventDefault();
        format(action);
      }
    }
  }

  /** Puts a reference on its own line at the caret (or the end when the text area is not focused). */
  insertReference.current = (markdown: string) => {
    const element = area.current;
    const start = element ? element.selectionStart : value.length;
    const end = element ? element.selectionEnd : value.length;
    const before = value.slice(0, start);
    const lead = before.length > 0 && !before.endsWith("\n") ? "\n" : "";
    const insert = `${lead}${markdown}\n`;
    apply({ start, end, insert, selectionStart: start + insert.length, selectionEnd: start + insert.length });
  };

  /** Adds files from a paste, a drop or the panel: each is uploaded, then referenced where the caret is. */
  async function attachFiles(files: File[]) {
    if (!attachmentOwner || files.length === 0) return;
    const api = attachmentApi;
    setAttachmentNote(files.length === 1 ? `Adding ${files[0].name || "the pasted file"}…` : `Adding ${files.length} files…`);
    const problems: string[] = [];
    for (const file of files) {
      try {
        const added = await uploadFile(api ?? attachmentRpc, attachmentOwner.kind, attachmentOwner.id, file);
        insertReference.current(attachmentMarkdown(added));
      } catch (error) {
        problems.push(`${file.name || "That file"}: ${error instanceof Error ? error.message : "could not be added."}`);
      }
    }
    setAttachmentKey((key) => key + 1);
    setAttachmentNote(problems.join(" "));
  }

  function drop(event: React.DragEvent<HTMLTextAreaElement>) {
    if (!attachmentOwner || !event.dataTransfer?.files.length) return;
    event.preventDefault();
    void attachFiles([...event.dataTransfer.files]);
  }

  function paste(event: React.ClipboardEvent<HTMLTextAreaElement>) {
    const files = attachmentOwner ? [...(event.clipboardData?.files ?? [])] : [];
    if (files.length > 0) {
      event.preventDefault();
      void attachFiles(files);
      return;
    }
    const html = event.clipboardData?.getData("text/html");
    if (!html) return; // plain text pastes natively
    const markdown = htmlToMarkdown(html);
    if (!markdown) return;
    event.preventDefault();
    const element = event.currentTarget;
    const start = element.selectionStart;
    apply({
      start,
      end: element.selectionEnd,
      insert: markdown,
      selectionStart: start + markdown.length,
      selectionEnd: start + markdown.length,
    });
  }

  const showList = mode === "source" && linkQuery !== null && candidates.length > 0;
  const editor = (
    <div
      data-slot="markdown-editor"
      data-expanded={expanded || undefined}
      role={expanded ? "dialog" : undefined}
      aria-label={expanded ? `${label} (expanded)` : undefined}
      className={cn(
        "grid min-w-0 gap-1.5",
        expanded &&
          "fixed top-[40px] right-0 bottom-0 left-0 z-40 grid-rows-[auto_auto_minmax(0,1fr)_auto] border-l bg-card px-[clamp(1rem,4vw,3rem)] py-6 shadow-2xl md:left-[15.5rem]",
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <label htmlFor={id} className={cn("text-xs font-medium text-muted-foreground", expanded && "text-sm")}>
          {label}
        </label>
        <div className="flex items-center gap-1">
          <Button
            type="button"
            variant={expanded ? "secondary" : "ghost"}
            size="icon-xs"
            className="text-muted-foreground"
            aria-pressed={expanded}
            aria-label={expanded ? "Collapse editor" : "Expand editor"}
            title={expanded ? "Collapse editor (Esc)" : "Expand editor to fill the window"}
            onMouseDown={(event) => event.preventDefault()}
            onClick={toggleExpanded}
          >
            {expanded ? <Minimize2 /> : <Maximize2 />}
          </Button>
          <div className="flex rounded-md border p-0.5" role="group" aria-label={`${label} view`}>
            <Button
              type="button"
              variant={mode === "source" ? "secondary" : "ghost"}
              size="xs"
              aria-pressed={mode === "source"}
              onClick={() => setMode("source")}
            >
              <PenLine />
              Write
            </Button>
            <Button
              type="button"
              variant={mode === "preview" ? "secondary" : "ghost"}
              size="xs"
              aria-pressed={mode === "preview"}
              onClick={() => {
                setLinkQuery(null);
                setMode("preview");
              }}
            >
              <Eye />
              Preview
            </Button>
          </div>
        </div>
      </div>
      {mode === "source" ? (
        <>
          <div role="toolbar" aria-label={`${label} formatting`} aria-controls={id} className="flex flex-wrap gap-0.5">
            {tools.map((tool) => (
              <Button
                key={tool.action}
                type="button"
                variant="ghost"
                size="icon-xs"
                className="text-muted-foreground"
                disabled={disabled}
                aria-label={tool.label}
                title={tool.shortcut ? `${tool.label} (${tool.shortcut})` : tool.label}
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => format(tool.action)}
              >
                {tool.icon}
              </Button>
            ))}
          </div>
          <div className={cn("relative", expanded && "min-h-0")}>
            <Textarea
              ref={area}
              id={id}
              rows={rows}
              value={value}
              disabled={disabled}
              placeholder={placeholder}
              spellCheck
              className={cn(
                "font-mono text-sm leading-relaxed",
                expanded ? "h-full min-h-0 resize-none [field-sizing:fixed]" : areaClassName,
                grow && !expanded && "[field-sizing:content]",
              )}
              dir="auto"
              role={searchLinks || mentionMembers.length ? "combobox" : undefined}
              aria-expanded={searchLinks || mentionMembers.length ? showList : undefined}
              aria-controls={showList ? listId : undefined}
              aria-autocomplete={searchLinks || mentionMembers.length ? "list" : undefined}
              aria-activedescendant={showList ? `${listId}-${active}` : undefined}
              onChange={(event) => {
                onChange(event.target.value);
                trackLinkQuery(event.target.value, event.target.selectionStart);
              }}
              onSelect={(event) => {
                trackLinkQuery(event.currentTarget.value, event.currentTarget.selectionStart);
                onSelectionChange?.(event.currentTarget.selectionStart, event.currentTarget.selectionEnd);
              }}
              autoFocus={autoFocus}
              onKeyDown={keyDown}
              onPaste={paste}
              onDrop={drop}
              onDragOver={(event) => {
                if (attachmentOwner && event.dataTransfer?.types.includes("Files")) event.preventDefault();
              }}
              onBlur={() => {
                setTimeout(() => setLinkQuery(null), 150);
                onBlur?.();
              }}
            />
            {showList && (
              <ul
                id={listId}
                role="listbox"
                aria-label={linkQuery?.mention ? "Mention a member" : "Link to a task, page or note"}
                className="absolute inset-x-0 top-full z-20 mt-1 grid max-h-56 overflow-y-auto rounded-md border bg-popover p-1 text-sm shadow-md"
              >
                {candidates.map((candidate, index) => (
                  <li
                    key={candidate.id}
                    id={`${listId}-${index}`}
                    role="option"
                    aria-selected={index === active}
                    className={cn("cursor-pointer truncate rounded-sm px-2 py-1.5", index === active && "bg-accent text-accent-foreground")}
                    onMouseDown={(event) => {
                      event.preventDefault();
                      pick(candidate);
                    }}
                  >
                    {candidate.kind && candidate.kind !== "task" && (
                      <span className="mr-1.5 text-xs text-muted-foreground">{candidateLabels[candidate.kind]}</span>
                    )}
                    {candidate.icon ? `${candidate.icon} ` : ""}
                    {candidate.title}
                    {candidate.detail && <span className="ml-1.5 text-xs text-muted-foreground">{candidate.detail}</span>}
                  </li>
                ))}
              </ul>
            )}
          </div>
          {hint ?? (
            <p className="text-[0.7rem] text-muted-foreground">
              Markdown. Type <kbd className="font-mono">[[</kbd> to link a task, page or note.
            </p>
          )}
        </>
      ) : (
        <div
          className={cn(
            "min-h-24 rounded-md border bg-background px-3 py-2 text-sm",
            expanded ? "row-span-2 min-h-0 overflow-y-auto" : areaClassName,
          )}
        >
          <MarkdownPreview source={value} links={previewLinks} />
        </div>
      )}
      {attachmentOwner && (
        <>
          <AttachmentPanel
            api={attachmentApi}
            ownerKind={attachmentOwner.kind}
            ownerId={attachmentOwner.id}
            reloadKey={attachmentKey}
            onInsert={mode === "source" ? (markdown) => insertReference.current(markdown) : undefined}
          />
          {attachmentNote && (
            <p role="status" className="text-xs text-muted-foreground">
              {attachmentNote}
            </p>
          )}
        </>
      )}
    </div>
  );

  if (!expanded) return editor;
  return (
    <>
      <div className="grid min-h-24 place-items-center rounded-md border border-dashed text-sm text-muted-foreground">
        <Button type="button" variant="ghost" size="sm" onClick={toggleExpanded}>
          <Minimize2 />
          Editing in the expanded view. Collapse
        </Button>
      </div>
      {createPortal(editor, document.body)}
    </>
  );
}
