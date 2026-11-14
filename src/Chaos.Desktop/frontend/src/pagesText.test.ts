import { test } from "node:test";
import assert from "node:assert/strict";
import { pageProperties, parseFrontMatter, setProperty, splitPage, joinPage, yamlScalar } from "./frontMatter";
import { continueList, headingsOf, indentItem, moveItem, outdentItem, wordCount } from "./outline";
import { lineDiff, diffStats } from "./lineDiff";
import type { TextEdit } from "./markdownEditing";

function applied(text: string, edit: TextEdit | null) {
  assert.ok(edit, "expected an edit");
  return { text: text.slice(0, edit.start) + edit.insert + text.slice(edit.end), start: edit.selectionStart, end: edit.selectionEnd };
}

test("front matter: reads values, flow and block lists, and keeps the body", () => {
  const text = "---\nbackground: lighthouse\nicon: \"🛠️\"\ntags: [home/renovation, '#errands']\naliases:\n  - Kitchen\n  - Cucina\nfoo: bar # comment\n---\n\n# Kitchen";
  const parsed = parseFrontMatter(text);
  assert.equal(parsed.error, null);
  assert.deepEqual(pageProperties(parsed), { icon: "🛠️", background: "lighthouse", tags: ["home/renovation", "errands"], aliases: ["Kitchen", "Cucina"] });
  assert.equal(parsed.properties.find(p => p.key === "foo")?.values[0], "bar");
  assert.equal(splitPage(text).body, "\n# Kitchen");
});

test("front matter: unreadable YAML reports the line and ignores every property", () => {
  const parsed = parseFrontMatter("---\nicon: x\ntags: [a\n---\nbody");
  assert.match(parsed.error ?? "", /Line 3/);
  assert.deepEqual(pageProperties(parsed).tags, []);
  assert.equal(splitPage("---\nicon: x\ntags: [a\n---\nbody").body, "body");
  // An unclosed block is not front matter at all: the whole page is the body.
  assert.equal(splitPage("---\nicon: x").body, "---\nicon: x");
});

test("front matter: setting one property leaves every other line untouched", () => {
  const text = "---\n# my notes\nfoo:   keep  me\naliases:\n  - A\nicon: x\n---\nbody";
  const changed = setProperty(text, "aliases", ["A", "B c"]);
  assert.equal(changed, "---\n# my notes\nfoo:   keep  me\naliases: [A, B c]\nicon: x\n---\nbody");
  assert.equal(setProperty(changed, "icon", null), "---\n# my notes\nfoo:   keep  me\naliases: [A, B c]\n---\nbody");
  assert.equal(setProperty("plain body", "background", "#2f6f4e"), "---\nbackground: \"#2f6f4e\"\n---\nplain body");
  // Unreadable YAML is never rewritten.
  assert.equal(setProperty("---\ntags: [a\n---\nb", "icon", "x"), "---\ntags: [a\n---\nb");
  assert.equal(joinPage("---\n---\n", "x"), "---\n---\nx");
  assert.equal(yamlScalar("yes"), "\"yes\"");
  assert.equal(yamlScalar("home/renovation"), "home/renovation");
});

test("outline: Tab and Shift+Tab move an item with its children", () => {
  const text = "- a\n- b\n  - c\n- d";
  const caret = text.indexOf("b");
  const indented = applied(text, indentItem(text, caret, caret));
  assert.equal(indented.text, "- a\n  - b\n    - c\n- d");
  assert.equal(indented.text[indented.start], "b");
  const back = applied(indented.text, outdentItem(indented.text, indented.start, indented.start));
  assert.equal(back.text, text);
  assert.equal(back.text[back.start], "b");
  assert.equal(indentItem("plain line", 2, 2), null);
});

test("outline: Alt+Up and Alt+Down swap an item and its children with a sibling", () => {
  const text = "- a\n  - a1\n- b\n  - b1\n- c";
  const caret = text.indexOf("b");
  const up = applied(text, moveItem(text, caret, caret, "up"));
  assert.equal(up.text, "- b\n  - b1\n- a\n  - a1\n- c");
  assert.equal(up.text[up.start], "b");
  const down = applied(text, moveItem(text, caret, caret, "down"));
  assert.equal(down.text, "- a\n  - a1\n- c\n- b\n  - b1");
  assert.equal(down.text[down.start], "b");
  assert.equal(moveItem(text, 2, 2, "up"), null); // first item
});

test("outline: Enter continues a list, and ends it on an empty item", () => {
  const text = "- [x] done";
  const next = applied(text, continueList(text, text.length, text.length));
  assert.equal(next.text, "- [x] done\n- [ ] ");
  const end = applied(next.text, continueList(next.text, next.text.length, next.text.length));
  assert.equal(end.text, "- [x] done\n");
  assert.equal(applied("1. one", continueList("1. one", 6, 6)).text, "1. one\n2. ");
});

test("headings and words", () => {
  assert.deepEqual(headingsOf("# A\ntext\n```\n# not\n```\n## B ##").map(h => [h.level, h.text]), [[1, "A"], [2, "B"]]);
  assert.equal(wordCount("Kitchen renovation: don't forget 3 quotes — مرحبا"), 7);
});

test("line diff lists removals before additions and keeps the rest", () => {
  const diff = lineDiff("a\nb\nc", "a\nB\nc\nd");
  assert.deepEqual(diff.map(line => `${line.kind[0]}${line.text}`), ["sa", "rb", "aB", "sc", "ad"]);
  assert.deepEqual(diffStats(diff), { added: 2, removed: 1 });
  assert.deepEqual(lineDiff("same", "same").map(line => line.kind), ["same"]);
});
