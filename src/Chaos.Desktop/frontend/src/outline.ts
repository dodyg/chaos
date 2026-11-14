/**
 * Outline editing in Markdown lists (SPEC Pages "Outline as you type"): Tab and Shift+Tab
 * indent and outdent the current list item with everything beneath it, Alt+Up and Alt+Down
 * move it past its sibling with everything beneath it. Pure text transforms: each returns the
 * edited range and the selection to restore, or null when the caret is not in a list item (so
 * Tab keeps its usual meaning there).
 */
import type { TextEdit } from "./markdownEditing";

const listItem = /^(\s*)([-*+]|\d+[.)])(\s+)/;
const indentUnit = "  ";

type Line = { start: number; end: number; text: string };

function splitLines(text: string): Line[] {
  const result: Line[] = [];
  let start = 0;
  for (;;) {
    const newline = text.indexOf("\n", start);
    const end = newline < 0 ? text.length : newline;
    result.push({ start, end, text: text.slice(start, end) });
    if (newline < 0) return result;
    start = newline + 1;
  }
}

function indentOf(line: string): number {
  const match = /^[ \t]*/.exec(line)![0];
  return match.replace(/\t/g, "    ").length;
}

function lineIndexAt(lines: Line[], offset: number): number {
  for (let index = 0; index < lines.length; index++) if (offset <= lines[index].end) return index;
  return lines.length - 1;
}

/** The list item at a line: its first line and the line after its last descendant. */
function itemBlock(lines: Line[], index: number): { first: number; after: number; indent: number } | null {
  let first = index;
  // A continuation line (indented text under an item) belongs to the item above it.
  while (first >= 0 && !listItem.test(lines[first].text)) {
    if (!lines[first].text.trim() || indentOf(lines[first].text) === 0) return null;
    first--;
  }
  if (first < 0) return null;
  const indent = indentOf(lines[first].text);
  let after = first + 1;
  while (after < lines.length) {
    const text = lines[after].text;
    if (!text.trim()) {
      // Blank lines belong to the item only when deeper content follows them.
      let next = after + 1;
      while (next < lines.length && !lines[next].text.trim()) next++;
      if (next < lines.length && indentOf(lines[next].text) > indent) { after = next; continue; }
      break;
    }
    if (indentOf(text) <= indent) break;
    after++;
  }
  return { first, after, indent };
}

function blockRange(lines: Line[], first: number, after: number) {
  return { start: lines[first].start, end: lines[after - 1].end };
}

/** Lines whose items the selection touches, as whole blocks. */
function selectedBlocks(lines: Line[], selectionStart: number, selectionEnd: number) {
  const startLine = lineIndexAt(lines, selectionStart);
  const endLine = lineIndexAt(lines, Math.max(selectionStart, selectionEnd - (selectionEnd > selectionStart ? 1 : 0)));
  const block = itemBlock(lines, startLine);
  if (!block) return null;
  let after = block.after;
  for (let index = block.after; index <= endLine && index < lines.length; index++) {
    const next = itemBlock(lines, index);
    if (next) after = Math.max(after, next.after);
  }
  return { first: block.first, after: Math.max(after, Math.min(endLine + 1, lines.length)), indent: block.indent };
}

function shift(selectionStart: number, selectionEnd: number, delta: (offset: number) => number) {
  return { selectionStart: delta(selectionStart), selectionEnd: delta(selectionEnd) };
}

/** Tab: the item (and its children) one level deeper, when an item sits above it to nest under. */
export function indentItem(text: string, selectionStart: number, selectionEnd: number): TextEdit | null {
  const lines = splitLines(text);
  const block = selectedBlocks(lines, selectionStart, selectionEnd);
  if (!block) return null;
  const range = blockRange(lines, block.first, block.after);
  const original = text.slice(range.start, range.end);
  const parts = original.split("\n");
  const replaced = parts.map(line => line.trim() ? indentUnit + line : line).join("\n");
  return {
    start: range.start, end: range.end, insert: replaced,
    ...shift(selectionStart, selectionEnd, offset => {
      if (offset < range.start) return offset;
      // Every non-blank line starting at or before the offset gained one indent.
      let added = 0;
      let position = range.start;
      for (const line of parts) {
        if (position > offset) break;
        if (line.trim()) added += indentUnit.length;
        position += line.length + 1;
      }
      return offset + added;
    }),
  };
}

/** Shift+Tab: the item (and its children) one level shallower; null at the top level. */
export function outdentItem(text: string, selectionStart: number, selectionEnd: number): TextEdit | null {
  const lines = splitLines(text);
  const block = selectedBlocks(lines, selectionStart, selectionEnd);
  if (!block || block.indent === 0) return block ? { start: selectionStart, end: selectionStart, insert: "", selectionStart, selectionEnd } : null;
  const range = blockRange(lines, block.first, block.after);
  const original = text.slice(range.start, range.end);
  const removeFrom = (line: string) => line.startsWith(indentUnit) ? indentUnit.length : line.startsWith("\t") ? 1 : line.startsWith(" ") ? 1 : 0;
  const parts = original.split("\n");
  const replaced = parts.map(line => line.slice(removeFrom(line))).join("\n");
  return {
    start: range.start, end: range.end, insert: replaced,
    ...shift(selectionStart, selectionEnd, offset => {
      if (offset < range.start) return offset;
      let consumed = 0;
      let removed = 0;
      for (const line of parts) {
        const lineEnd = range.start + consumed + line.length;
        const cut = removeFrom(line);
        if (offset <= lineEnd) { removed += Math.min(cut, offset - (range.start + consumed)); break; }
        removed += cut;
        consumed += line.length + 1;
      }
      return offset - removed;
    }),
  };
}

/** Alt+Up / Alt+Down: swaps the item (with its children) with the sibling before or after it. */
export function moveItem(text: string, selectionStart: number, selectionEnd: number, direction: "up" | "down"): TextEdit | null {
  const lines = splitLines(text);
  const block = itemBlock(lines, lineIndexAt(lines, selectionStart));
  if (!block) return null;
  let other: { first: number; after: number } | null = null;
  if (direction === "up") {
    // The previous sibling: walk up past deeper lines to an item at the same indent.
    for (let index = block.first - 1; index >= 0; index--) {
      const textLine = lines[index].text;
      if (!textLine.trim()) continue;
      const indent = indentOf(textLine);
      if (indent < block.indent) break;
      if (indent === block.indent) {
        const candidate = itemBlock(lines, index);
        if (candidate && candidate.indent === block.indent && candidate.after >= block.first) other = candidate;
        break;
      }
    }
  } else {
    let next = block.after;
    while (next < lines.length && !lines[next].text.trim()) next++;
    if (next < lines.length && indentOf(lines[next].text) === block.indent && listItem.test(lines[next].text)) other = itemBlock(lines, next);
  }
  if (!other) return null;
  const upper = direction === "up" ? other : block;
  const lower = direction === "up" ? block : other;
  const upperText = text.slice(lines[upper.first].start, lines[upper.after - 1].end);
  const gap = text.slice(lines[upper.after - 1].end, lines[lower.first].start);
  const lowerText = text.slice(lines[lower.first].start, lines[lower.after - 1].end);
  const start = lines[upper.first].start;
  const end = lines[lower.after - 1].end;
  const insert = lowerText + gap + upperText;
  const caretOffset = selectionStart - lines[block.first].start;
  const selectionLength = selectionEnd - selectionStart;
  const movedStart = direction === "up" ? start : start + lowerText.length + gap.length;
  return { start, end, insert, selectionStart: movedStart + caretOffset, selectionEnd: movedStart + caretOffset + selectionLength };
}

/** Enter at the end of a list item continues the list with the same marker (and a fresh checkbox). */
export function continueList(text: string, selectionStart: number, selectionEnd: number): TextEdit | null {
  if (selectionStart !== selectionEnd) return null;
  const lineStart = text.lastIndexOf("\n", selectionStart - 1) + 1;
  const newline = text.indexOf("\n", selectionStart);
  const lineEnd = newline < 0 ? text.length : newline;
  const line = text.slice(lineStart, lineEnd);
  if (selectionStart !== lineEnd) return null;
  const match = /^(\s*)([-*+]|(\d+)([.)]))(\s+)(\[[ xX]\]\s+)?(.*)$/.exec(line);
  if (!match) return null;
  const [, indent, marker, number, delimiter, space, checkbox, rest] = match;
  if (!rest.trim()) {
    // Enter on an empty item ends the list (outdenting first when nested).
    return { start: lineStart, end: lineEnd, insert: "", selectionStart: lineStart, selectionEnd: lineStart };
  }
  const nextMarker = number ? `${Number(number) + 1}${delimiter}` : marker;
  const insert = `\n${indent}${nextMarker}${space}${checkbox ? "[ ] " : ""}`;
  return { start: selectionStart, end: selectionEnd, insert, selectionStart: selectionStart + insert.length, selectionEnd: selectionStart + insert.length };
}

/** Headings of a Markdown body in order (outside code fences), with their line offsets. */
export function headingsOf(body: string): { level: number; text: string; start: number; end: number }[] {
  const result: { level: number; text: string; start: number; end: number }[] = [];
  let fence: string | null = null;
  for (const line of splitLines(body)) {
    const trimmed = line.text.trimStart();
    const fenceMatch = /^(`{3,}|~{3,})/.exec(trimmed);
    if (fenceMatch) { fence = fence === null ? fenceMatch[1][0] : fence === fenceMatch[1][0] ? null : fence; continue; }
    if (fence !== null) continue;
    const match = /^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$/.exec(line.text);
    if (match) result.push({ level: match[1].length, text: match[2], start: line.start, end: line.end });
  }
  return result;
}

/** Words in a text: runs of letters or digits, as the server counts them. */
export function wordCount(text: string): number {
  return text.match(/[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*/gu)?.length ?? 0;
}
