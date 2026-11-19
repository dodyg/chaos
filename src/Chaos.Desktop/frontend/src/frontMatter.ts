/**
 * Page front matter (SPEC Pages): a YAML block between two `---` lines at the very top of a
 * page. Only the part of YAML pages need is read — `key: value` lines with plain, quoted or
 * `[flow, list]` values, block lists of `- item` lines and comments — matching the server's
 * PageFrontMatter. Editing one property rewrites only that key's lines; every other line,
 * comment and unknown key stays exactly as written. Front matter is data and never run.
 */

export type FrontMatterProperty = { key: string; values: string[]; raw: string; list: boolean };

export type FrontMatter = {
  /** The page starts with a `---` line that is closed by another `---` line. */
  present: boolean;
  /** Why the block cannot be read; its properties are then ignored until it is fixed. */
  error: string | null;
  properties: FrontMatterProperty[];
  /** The YAML between the two lines, exactly as written. */
  yaml: string;
  /** Offset in the page text where the body starts (0 without front matter). */
  bodyStart: number;
};

const none: FrontMatter = { present: false, error: null, properties: [], yaml: "", bodyStart: 0 };

type Line = { text: string; start: number; end: number };

function lines(text: string): Line[] {
  const result: Line[] = [];
  let start = 0;
  while (start < text.length) {
    const newline = text.indexOf("\n", start);
    const end = newline < 0 ? text.length : newline + 1;
    result.push({ text: text.slice(start, newline < 0 ? text.length : newline).replace(/\r$/, ""), start, end });
    start = end;
  }
  return result;
}

function unquote(value: string): { value: string; error?: string } {
  if (value.startsWith('"') || value.startsWith("'")) {
    const quote = value[0];
    if (value.length < 2 || !value.endsWith(quote)) return { value, error: "A quoted value is not closed." };
    const inner = value.slice(1, -1);
    return { value: quote === '"' ? inner.replace(/\\"/g, '"').replace(/\\\\/g, "\\") : inner.replace(/''/g, "'") };
  }
  return { value };
}

function commentStart(value: string): number {
  let quote = "";
  for (let index = 0; index < value.length; index++) {
    const character = value[index];
    if (quote) {
      if (character === quote) quote = "";
    } else if (character === '"' || character === "'") quote = character;
    else if (character === "#" && index > 0 && /\s/.test(value[index - 1])) return index;
  }
  return -1;
}

function splitFlow(inner: string): string[] {
  const parts: string[] = [];
  let start = 0;
  let quote = "";
  for (let index = 0; index < inner.length; index++) {
    const character = inner[index];
    if (quote) {
      if (character === quote) quote = "";
    } else if (character === '"' || character === "'") quote = character;
    else if (character === ",") {
      parts.push(inner.slice(start, index));
      start = index + 1;
    }
  }
  parts.push(inner.slice(start));
  return parts;
}

function keyColon(line: string): number {
  let quote = "";
  for (let index = 0; index < line.length; index++) {
    const character = line[index];
    if (quote) {
      if (character === quote) quote = "";
      continue;
    }
    if ((character === '"' || character === "'") && index === 0) {
      quote = character;
      continue;
    }
    if (character === ":" && (index + 1 === line.length || line[index + 1] === " " || line[index + 1] === "\t")) return index;
  }
  return -1;
}

export function parseFrontMatter(text: string): FrontMatter {
  const all = lines(text);
  if (all.length === 0 || all[0].text.trimEnd() !== "---") return none;
  const close = all.findIndex((line, index) => index > 0 && (line.text.trimEnd() === "---" || line.text.trimEnd() === "..."));
  if (close < 0) return { ...none, present: false, error: "The front matter has no closing --- line." };
  const bodyStart = all[close].end;
  const yaml = text.slice(all[0].end, all[close].start).replace(/\n$/, "");
  const fail = (index: number, message: string): FrontMatter => ({
    present: true,
    error: `Line ${index + 1}: ${message}`,
    properties: [],
    yaml,
    bodyStart,
  });
  const properties: FrontMatterProperty[] = [];
  let openList: FrontMatterProperty | null = null;
  for (let index = 1; index < close; index++) {
    const line = all[index].text.trimEnd();
    const trimmed = line.trimStart();
    if (!trimmed || trimmed.startsWith("#")) continue;
    if (trimmed === "-" || trimmed.startsWith("- ")) {
      if (!openList) return fail(index, "A list item has no key above it.");
      const item = trimmed === "-" ? { value: "" } : unquote(trimmed.slice(2).trim());
      if (item.error) return fail(index, item.error);
      openList.values.push(item.value);
      continue;
    }
    if (/^\s/.test(line)) return fail(index, "Nested values are not supported. Use key: value or a list.");
    const colon = keyColon(line);
    if (colon <= 0) return fail(index, "Expected key: value.");
    let key = line.slice(0, colon).trim();
    if (key.startsWith('"') || key.startsWith("'")) key = unquote(key).value;
    if (properties.some((property) => property.key === key)) return fail(index, `The key "${key}" appears twice.`);
    let raw = line.slice(colon + 1).trim();
    const comment = commentStart(raw);
    if (comment >= 0) raw = raw.slice(0, comment).trimEnd();
    let values: string[];
    let list = false;
    if (!raw) {
      values = [];
      list = true;
    } else if (raw.startsWith("[")) {
      if (!raw.endsWith("]")) return fail(index, "A [list] is not closed.");
      values = [];
      list = true;
      for (const part of splitFlow(raw.slice(1, -1))) {
        const parsed = unquote(part.trim());
        if (parsed.error) return fail(index, parsed.error);
        if (parsed.value || part.trim()) values.push(parsed.value);
      }
    } else if (raw.startsWith("{")) return fail(index, "Nested values are not supported. Use key: value or a list.");
    else {
      const parsed = unquote(raw);
      if (parsed.error) return fail(index, parsed.error);
      values = [parsed.value];
    }
    const property: FrontMatterProperty = { key, values, raw, list };
    properties.push(property);
    openList = raw ? null : property;
  }
  return { present: true, error: null, properties, yaml, bodyStart };
}

export function frontMatterValue(frontMatter: FrontMatter, key: string): string | null {
  if (frontMatter.error) return null;
  const property = frontMatter.properties.find((item) => item.key === key);
  return property && property.values.length === 1 ? property.values[0] : null;
}

export function frontMatterList(frontMatter: FrontMatter, key: string): string[] {
  if (frontMatter.error) return [];
  return frontMatter.properties.find((item) => item.key === key)?.values ?? [];
}

/** The page text split into its front matter block (with both `---` lines) and its body. */
export function splitPage(text: string): { head: string; body: string; frontMatter: FrontMatter } {
  const frontMatter = parseFrontMatter(text);
  if (!frontMatter.present) return { head: "", body: text, frontMatter };
  return { head: text.slice(0, frontMatter.bodyStart), body: text.slice(frontMatter.bodyStart), frontMatter };
}

/** Joins a front matter block (from {@link splitPage} or {@link withYaml}) and a body. */
export function joinPage(head: string, body: string): string {
  if (!head) return body;
  return head.endsWith("\n") ? head + body : `${head}\n${body}`;
}

/** A front matter block holding exactly this YAML; empty YAML keeps an empty block. */
export function withYaml(yaml: string): string {
  const inner = yaml.replace(/\n+$/, "");
  return inner ? `---\n${inner}\n---\n` : "---\n---\n";
}

const plainScalar = /^[^\s"'#[\]{},&*!|>%@`:-][^#:]*$/;

/** A YAML value written the simplest safe way: plain when it can be, quoted otherwise. */
export function yamlScalar(value: string): string {
  if (value === "") return '""';
  if (plainScalar.test(value) && value.trim() === value && !/^(true|false|null|yes|no|~)$/i.test(value) && !/^[\d.+-]/.test(value))
    return value;
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

/**
 * Sets one property in a page's front matter, adding the block when the page has none. Only
 * that key's lines change; `null` removes the key. Unreadable YAML is never rewritten.
 */
export function setProperty(text: string, key: string, value: string | string[] | null): string {
  const { head, body, frontMatter } = splitPage(text);
  if (frontMatter.error) return text;
  const rendered =
    value === null ? null : Array.isArray(value) ? `${key}: [${value.map(yamlScalar).join(", ")}]` : `${key}: ${yamlScalar(value)}`;
  if (!frontMatter.present) return rendered === null ? text : joinPage(withYaml(rendered), body);
  const yamlLines = frontMatter.yaml ? frontMatter.yaml.split("\n") : [];
  const start = yamlLines.findIndex(
    (line) => !/^\s/.test(line) && keyColon(line) > 0 && unquote(line.slice(0, keyColon(line)).trim()).value === key,
  );
  if (start < 0) {
    if (rendered === null) return text;
    yamlLines.push(rendered);
  } else {
    let end = start + 1;
    while (
      end < yamlLines.length &&
      (/^\s/.test(yamlLines[end]) || yamlLines[end].trimStart().startsWith("- ") || yamlLines[end].trim() === "-")
    )
      end++;
    yamlLines.splice(start, end - start, ...(rendered === null ? [] : [rendered]));
  }
  void head;
  return joinPage(withYaml(yamlLines.join("\n")), body);
}

/** The page's icon, background, tags and aliases as the app reads them. */
export function pageProperties(frontMatter: FrontMatter) {
  return {
    icon: frontMatterValue(frontMatter, "icon"),
    background: frontMatterValue(frontMatter, "background"),
    tags: frontMatterList(frontMatter, "tags")
      .map((tag) => tag.replace(/^#/, "").trim())
      .filter(Boolean),
    aliases: frontMatterList(frontMatter, "aliases")
      .map((alias) => alias.trim())
      .filter(Boolean),
  };
}

/** Keys with their own editor in the properties panel. */
export const knownKeys = ["icon", "background", "tags", "aliases"] as const;
