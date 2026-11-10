/**
 * Pure text transforms behind the Markdown toolbar (PLAN §9): each acts on the actual
 * selection and returns the replaced range plus the selection to restore, so the editor can
 * apply it as one native edit (keeping the textarea's own undo history and caret).
 */
export type FormatAction = "heading" | "bold" | "italic" | "strike" | "bullets" | "numbers" | "checklist" | "quote" | "code" | "codeBlock" | "link" | "table";

export type TextEdit = {
  /** Range of the original text to replace. */
  start: number;
  end: number;
  insert: string;
  /** Selection after the edit, in the new text. */
  selectionStart: number;
  selectionEnd: number;
};

const wrappers: Partial<Record<FormatAction, [string, string, string]>> = {
  bold: ["**", "**", "bold text"],
  italic: ["*", "*", "italic text"],
  strike: ["~~", "~~", "struck text"],
  code: ["`", "`", "code"],
};

const linePrefixes: Partial<Record<FormatAction, (index: number) => string>> = {
  heading: () => "## ",
  bullets: () => "- ",
  numbers: index => `${index + 1}. `,
  checklist: () => "- [ ] ",
  quote: () => "> ",
};

export function formatEdit(text: string, selectionStart: number, selectionEnd: number, action: FormatAction): TextEdit {
  const start = Math.min(selectionStart, selectionEnd);
  const end = Math.max(selectionStart, selectionEnd);
  const selected = text.slice(start, end);

  const wrapper = wrappers[action];
  if (wrapper) {
    const [open, close, placeholder] = wrapper;
    // Toggle off when the selection is already wrapped.
    if (selected.startsWith(open) && selected.endsWith(close) && selected.length >= open.length + close.length) {
      const inner = selected.slice(open.length, selected.length - close.length);
      return { start, end, insert: inner, selectionStart: start, selectionEnd: start + inner.length };
    }
    const inner = selected || placeholder;
    return { start, end, insert: `${open}${inner}${close}`, selectionStart: start + open.length, selectionEnd: start + open.length + inner.length };
  }

  const prefix = linePrefixes[action];
  if (prefix) {
    const lineStart = text.lastIndexOf("\n", start - 1) + 1;
    const nextBreak = text.indexOf("\n", end > start && text[end - 1] === "\n" ? end - 1 : end);
    const lineEnd = nextBreak === -1 ? text.length : nextBreak;
    const lines = text.slice(lineStart, lineEnd).split("\n");
    const prefixes = lines.map((_, index) => prefix(index));
    const allPrefixed = lines.every((line, index) => line.startsWith(prefixes[index]));
    const replaced = lines.map((line, index) => allPrefixed ? line.slice(prefixes[index].length) : `${prefixes[index]}${line}`).join("\n");
    return { start: lineStart, end: lineEnd, insert: replaced, selectionStart: lineStart, selectionEnd: lineStart + replaced.length };
  }

  if (action === "codeBlock") {
    const body = selected || "code";
    const before = start > 0 && text[start - 1] !== "\n" ? "\n" : "";
    const insert = `${before}\`\`\`\n${body}\n\`\`\`\n`;
    const bodyStart = start + before.length + 4;
    return { start, end, insert, selectionStart: bodyStart, selectionEnd: bodyStart + body.length };
  }

  if (action === "link") {
    const label = selected || "link text";
    const insert = `[${label}](https://)`;
    const urlStart = start + label.length + 3;
    return { start, end, insert, selectionStart: urlStart, selectionEnd: urlStart + "https://".length };
  }

  // table
  const before = start > 0 && text[start - 1] !== "\n" ? "\n\n" : "";
  const insert = `${before}| Column | Column |\n| --- | --- |\n| ${selected || "Cell"} | Cell |\n`;
  return { start, end, insert, selectionStart: start + before.length + 2, selectionEnd: start + before.length + 8 };
}

/** The `[[query` being typed right before the caret, for the internal-link autocomplete. */
export function linkQueryAt(text: string, caret: number): { start: number; query: string } | null {
  const match = /\[\[([^\]\n[]{0,60})$/.exec(text.slice(Math.max(0, caret - 64), caret));
  if (!match) return null;
  return { start: caret - match[0].length, query: match[1] };
}

/** Markdown for an internal task link: a readable label plus the stable ID. */
export function taskLinkMarkdown(title: string, taskId: string): string {
  const label = title.replace(/[[\]\\]/g, character => `\\${character}`);
  return `[${label}](chaos:task/${taskId})`;
}

/** Markdown for an internal pad-note link: a readable label plus the stable note ID. */
export function padLinkMarkdown(label: string, itemId: string): string {
  const text = label.replace(/[[\]\\]/g, character => `\\${character}`);
  return `[${text}](chaos:pad/${itemId})`;
}
