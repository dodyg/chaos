export type Theme = { name: string; font: string; size: number; accent: string };
export const defaultTheme: Theme = { name: "Default", font: "system", size: 16, accent: "#c45a20" };
export const fonts: Record<string, string> = {
  system: '"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif',
  sans: 'Arial, sans-serif',
  serif: 'Georgia, serif',
  mono: 'Consolas, monospace',
  calibri: 'Calibri, "Segoe UI", sans-serif',
  tahoma: 'Tahoma, "Segoe UI", sans-serif',
  trebuchet: '"Trebuchet MS", "Segoe UI", sans-serif',
  verdana: 'Verdana, "Segoe UI", sans-serif',
};
const activeKey = "chaos.appearance.active.v1";
const themesKey = "chaos.appearance.themes.v1";

export function parseTheme(value: unknown): Theme {
  if (!value || typeof value !== "object") throw new Error("Invalid theme file.");
  const theme = value as Record<string, unknown>;
  if (typeof theme.name !== "string" || !theme.name.trim() || theme.name.length > 80 ||
      typeof theme.font !== "string" || !Object.hasOwn(fonts, theme.font) ||
      typeof theme.size !== "number" || !Number.isInteger(theme.size) || theme.size < 12 || theme.size > 22 ||
      typeof theme.accent !== "string" || !/^#[0-9a-f]{6}$/i.test(theme.accent)) throw new Error("Invalid theme: check its name, font, size and accent color.");
  return { name: theme.name.trim(), font: theme.font, size: theme.size, accent: theme.accent };
}
export function applyTheme(theme: Theme) {
  const root = document.documentElement;
  root.style.setProperty("--appearance-font", fonts[theme.font]);
  root.style.fontSize = `${theme.size}px`;
  root.style.setProperty("--primary", theme.accent);
  root.style.setProperty("--ring", theme.accent);
  root.style.setProperty("--sidebar-primary", theme.accent);
  root.style.setProperty("--sidebar-ring", theme.accent);
  const rgb = [1, 3, 5].map(i => parseInt(theme.accent.slice(i, i + 2), 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4);
  const foreground = .2126 * rgb[0] + .7152 * rgb[1] + .0722 * rgb[2] > .179 ? "#000000" : "#ffffff";
  root.style.setProperty("--primary-foreground", foreground);
  root.style.setProperty("--sidebar-primary-foreground", foreground);
}
export function loadActiveTheme(): Theme {
  try { const raw = localStorage.getItem(activeKey); return raw ? parseTheme(JSON.parse(raw)) : defaultTheme; }
  catch { return defaultTheme; }
}
export function saveActiveTheme(theme: Theme) { localStorage.setItem(activeKey, JSON.stringify(theme)); }
export function loadThemes(): Theme[] {
  try { const raw = localStorage.getItem(themesKey); const values: unknown = raw ? JSON.parse(raw) : []; return Array.isArray(values) ? values.map(parseTheme) : []; }
  catch { return []; }
}
export function saveThemes(themes: Theme[]) { localStorage.setItem(themesKey, JSON.stringify(themes)); }
