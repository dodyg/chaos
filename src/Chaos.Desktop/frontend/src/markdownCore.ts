/**
 * The Markdown rules the desktop app and the mobile PWA must agree on. Their renderers differ (the desktop walks the
 * mdast tree itself, the PWA uses react-markdown), so what is shared here is what decides what a note may do:
 * which links are clickable and how Mermaid output survives the apps' CSP. Pure code with no imports, so both
 * apps (and their node tests) can load it by relative path.
 */

/** External URLs a note link may open; everything else renders as inert text. */
export function isSafeExternalUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:" || parsed.protocol === "mailto:";
  } catch { return false; }
}

/** Splits CSS declaration text on top-level semicolons (not those inside parentheses or quotes). */
export function declarationsOf(css: string): Array<[string, string, string]> {
  const parts: string[] = [];
  let depth = 0, quote = "", start = 0;
  for (let index = 0; index < css.length; index++) {
    const ch = css[index];
    if (quote) { if (ch === quote) quote = ""; continue; }
    if (ch === "\"" || ch === "'") quote = ch;
    else if (ch === "(") depth++;
    else if (ch === ")") depth = Math.max(0, depth - 1);
    else if (ch === ";" && depth === 0) { parts.push(css.slice(start, index)); start = index + 1; }
  }
  parts.push(css.slice(start));
  const result: Array<[string, string, string]> = [];
  for (const part of parts) {
    const colon = part.indexOf(":");
    if (colon <= 0) continue;
    let value = part.slice(colon + 1).trim();
    const important = /\s*!important$/i.test(value);
    if (important) value = value.replace(/\s*!important$/i, "");
    result.push([part.slice(0, colon).trim(), value, important ? "important" : ""]);
  }
  return result;
}

/**
 * Both apps' CSP (`style-src 'self'`) drops inline `style="…"` attributes parsed from markup, which is where pele defines
 * its colour variables, so fills would fall back to black. The attribute text stays in the DOM, though, and setting each
 * declaration one property at a time through the CSSOM is allowed, so each render copies them across.
 */
export function reapplyInlineStyles(root: ParentNode) {
  for (const element of root.querySelectorAll<HTMLElement | SVGElement>("[style]")) {
    const css = element.getAttribute("style");
    if (!css) continue;
    for (const [name, value, priority] of declarationsOf(css)) element.style.setProperty(name, value, priority);
  }
}

/** The words shown when a Mermaid diagram cannot be drawn. */
export function diagramFailureMessage(failure: unknown): string {
  return failure instanceof Error ? failure.message : "Could not draw this diagram.";
}
