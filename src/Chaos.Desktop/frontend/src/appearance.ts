/**
 * Appearance themes: fonts, size, density, corners, light/dark mode, accent and surface colours.
 * A theme is plain JSON so it can be saved, exported and imported; every value is validated
 * before it reaches the page, and only hex colours and known keys are ever written into styles.
 */
export type Mode = "system" | "light" | "dark";
export type Scheme = "light" | "dark";
export const colorKeys = ["background", "surface", "sidebar", "hover", "text", "mutedText", "border"] as const;
export type ColorKey = typeof colorKeys[number];
export type Palette = Partial<Record<ColorKey, string>>;
export type Theme = {
  name: string; font: string; codeFont: string; size: number; accent: string;
  mode: Mode; density: string; radius: string; light: Palette; dark: Palette;
};

export const fonts: Record<string, string> = {
  system: '"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif',
  sans: 'Arial, sans-serif',
  serif: 'Georgia, serif',
  mono: 'Consolas, monospace',
  calibri: 'Calibri, "Segoe UI", sans-serif',
  tahoma: 'Tahoma, "Segoe UI", sans-serif',
  trebuchet: '"Trebuchet MS", "Segoe UI", sans-serif',
  verdana: 'Verdana, "Segoe UI", sans-serif',
  bahnschrift: 'Bahnschrift, "Segoe UI", sans-serif',
  candara: 'Candara, "Segoe UI", sans-serif',
  cambria: 'Cambria, Georgia, serif',
  constantia: 'Constantia, Georgia, serif',
  palatino: '"Palatino Linotype", Palatino, Georgia, serif',
  sitka: '"Sitka Text", Georgia, serif',
  cascadia: '"Cascadia Code", Consolas, monospace',
};
export const fontNames: Record<string, string> = {
  system: "System", sans: "Arial", serif: "Georgia", mono: "Consolas", calibri: "Calibri", tahoma: "Tahoma",
  trebuchet: "Trebuchet MS", verdana: "Verdana", bahnschrift: "Bahnschrift", candara: "Candara", cambria: "Cambria",
  constantia: "Constantia", palatino: "Palatino", sitka: "Sitka", cascadia: "Cascadia Code",
};
export const codeFonts: Record<string, { label: string; family: string }> = {
  consolas: { label: "Consolas", family: 'Consolas, ui-monospace, monospace' },
  cascadia: { label: "Cascadia Code", family: '"Cascadia Code", Consolas, monospace' },
  cascadiaMono: { label: "Cascadia Mono", family: '"Cascadia Mono", Consolas, monospace' },
  courier: { label: "Courier New", family: '"Courier New", Courier, monospace' },
  lucida: { label: "Lucida Console", family: '"Lucida Console", Consolas, monospace' },
};
export const densities: Record<string, { label: string; spacing: string }> = {
  compact: { label: "Compact", spacing: "0.22rem" },
  comfortable: { label: "Comfortable", spacing: "0.25rem" },
  spacious: { label: "Spacious", spacing: "0.285rem" },
};
export const radii: Record<string, { label: string; value: string }> = {
  none: { label: "Square", value: "0rem" },
  small: { label: "Subtle", value: "0.3rem" },
  medium: { label: "Rounded", value: "0.625rem" },
  large: { label: "Soft", value: "0.9rem" },
  round: { label: "Pill", value: "1.3rem" },
};
export const colorLabels: Record<ColorKey, string> = {
  background: "Background", surface: "Cards and panels", sidebar: "Sidebar", hover: "Hover and fills",
  text: "Text", mutedText: "Secondary text", border: "Borders",
};
/** Hex approximations of the built-in stone palette, shown when a colour has no override. */
export const basePalettes: Record<Scheme, Required<Palette>> = {
  light: { background: "#fafaf9", surface: "#ffffff", sidebar: "#f3f3f1", hover: "#f0f0ee", text: "#0c0a09", mutedText: "#6f6863", border: "#e5e3e1" },
  dark: { background: "#0f0d0c", surface: "#1c1a19", sidebar: "#151312", hover: "#2a2725", text: "#f2f2f0", mutedText: "#a8a29e", border: "#2e2b29" },
};
/** Each palette colour feeds the shadcn tokens that share its role. */
const colorVars: Record<ColorKey, string[]> = {
  background: ["--background"],
  surface: ["--card", "--popover"],
  sidebar: ["--sidebar"],
  hover: ["--secondary", "--muted", "--accent", "--sidebar-accent"],
  text: ["--foreground", "--card-foreground", "--popover-foreground", "--secondary-foreground", "--accent-foreground", "--sidebar-foreground", "--sidebar-accent-foreground"],
  mutedText: ["--muted-foreground"],
  border: ["--border", "--input", "--sidebar-border"],
};

export const defaultTheme: Theme = { name: "Default", font: "system", codeFont: "consolas", size: 16, accent: "#c45a20", mode: "system", density: "comfortable", radius: "medium", light: {}, dark: {} };

const preset = (theme: Partial<Theme> & Pick<Theme, "name">): Theme => ({ ...defaultTheme, ...theme });
/** Ready-made themes. Dark-only and light-only themes pin their mode. */
export const presetThemes: Theme[] = [
  defaultTheme,
  preset({ name: "Paper", font: "constantia", accent: "#8a5a2b", mode: "light", radius: "small",
    light: { background: "#f6f0e4", surface: "#fbf7ef", sidebar: "#efe6d6", hover: "#e9dfcc", text: "#3b2f22", mutedText: "#6a5a44", border: "#ddd0b9" } }),
  preset({ name: "Midnight", accent: "#5b8def", mode: "dark",
    dark: { background: "#0f1424", surface: "#161d33", sidebar: "#121829", hover: "#1f2842", text: "#e3e8f5", mutedText: "#8f99b3", border: "#263052" } }),
  preset({ name: "Arctic", font: "bahnschrift", accent: "#5e81ac", radius: "small",
    light: { background: "#eceff4", surface: "#f7f9fb", sidebar: "#e5e9f0", hover: "#dde3eb", text: "#2e3440", mutedText: "#56627a", border: "#d3d9e3" },
    dark: { background: "#2e3440", surface: "#3b4252", sidebar: "#333a47", hover: "#434c5e", text: "#eceff4", mutedText: "#b4bccb", border: "#4c566a" } }),
  preset({ name: "Solar", accent: "#268bd2",
    light: { background: "#fdf6e3", surface: "#fffbef", sidebar: "#eee8d5", hover: "#e8e1cb", text: "#2f3f45", mutedText: "#4f6165", border: "#e0d8bf" },
    dark: { background: "#002b36", surface: "#073642", sidebar: "#04313c", hover: "#0d4250", text: "#e0e6e2", mutedText: "#a5b2b2", border: "#154854" } }),
  preset({ name: "Forest", accent: "#3fa66b", mode: "dark", radius: "large",
    dark: { background: "#121a16", surface: "#18231d", sidebar: "#141e18", hover: "#22302a", text: "#e0ebe4", mutedText: "#93a99c", border: "#2a3a31" } }),
  preset({ name: "Ocean", font: "calibri", accent: "#00877f",
    light: { background: "#f2f8f9", surface: "#fbfefe", sidebar: "#e6f2f4", hover: "#d9ecef", text: "#12303a", mutedText: "#4c6c75", border: "#cfe3e7" },
    dark: { background: "#0d1d22", surface: "#13282f", sidebar: "#10232a", hover: "#1b3640", text: "#dff0f3", mutedText: "#8aabb3", border: "#23404a" } }),
  preset({ name: "Lavender", font: "candara", accent: "#8052b5", radius: "large",
    light: { background: "#f7f5fc", surface: "#fdfcff", sidebar: "#efebf9", hover: "#e7e1f6", text: "#2a2340", mutedText: "#6a6184", border: "#ddd6f0" },
    dark: { background: "#1a1726", surface: "#221e31", sidebar: "#1d1a2b", hover: "#2d2840", text: "#ece8f7", mutedText: "#a69fbe", border: "#352f4b" } }),
  preset({ name: "Blossom", accent: "#be3567", mode: "light", radius: "round",
    light: { background: "#fdf5f7", surface: "#fffafb", sidebar: "#f9e8ed", hover: "#f5dde5", text: "#3a2430", mutedText: "#7a5664", border: "#efd3dc" } }),
  preset({ name: "Mocha", font: "cambria", accent: "#c77d3a", mode: "dark",
    dark: { background: "#1e1714", surface: "#271e1a", sidebar: "#221a16", hover: "#33281f", text: "#f1e6dc", mutedText: "#b39f8e", border: "#3d2f27" } }),
  preset({ name: "Graphite", font: "tahoma", accent: "#607487", density: "compact", radius: "small",
    light: { background: "#f4f4f5", surface: "#ffffff", sidebar: "#ececee", hover: "#e4e4e7", text: "#18181b", mutedText: "#5c5c66", border: "#dcdce0" },
    dark: { background: "#18181b", surface: "#212125", sidebar: "#1c1c1f", hover: "#2c2c31", text: "#f0f0f2", mutedText: "#a1a1aa", border: "#34343a" } }),
  preset({ name: "Terminal", font: "cascadia", codeFont: "cascadia", accent: "#2fbf5b", mode: "dark", density: "compact", radius: "none",
    dark: { background: "#0b0f0c", surface: "#111712", sidebar: "#0e130f", hover: "#18211a", text: "#c8f0d0", mutedText: "#78a381", border: "#1f2d22" } }),
  preset({ name: "High contrast", accent: "#1f6fd6", size: 17,
    light: { background: "#ffffff", surface: "#ffffff", sidebar: "#f2f2f2", hover: "#e3e3e3", text: "#000000", mutedText: "#333333", border: "#6b6b6b" },
    dark: { background: "#000000", surface: "#0a0a0a", sidebar: "#050505", hover: "#262626", text: "#ffffff", mutedText: "#d4d4d4", border: "#8a8a8a" } }),
  preset({ name: "Roomy", font: "sitka", size: 17, accent: "#495bc1", density: "spacious", radius: "large" }),
];

const activeKey = "chaos.appearance.active.v1";
const themesKey = "chaos.appearance.themes.v1";
const hex = /^#[0-9a-f]{6}$/i;
const modes: Mode[] = ["system", "light", "dark"];

function parsePalette(value: unknown): Palette {
  if (value === undefined) return {};
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid theme: its colors must be a list of hex colors.");
  const entries = value as Record<string, unknown>;
  for (const [key, color] of Object.entries(entries))
    if (!(colorKeys as readonly string[]).includes(key) || typeof color !== "string" || !hex.test(color)) throw new Error(`Invalid theme: color "${key}" must be a known color set to a hex value like #1a2b3c.`);
  const palette: Palette = {};
  for (const key of colorKeys) if (typeof entries[key] === "string") palette[key] = (entries[key] as string).toLowerCase();
  return palette;
}
/** Validates a theme. Older themes (name, font, size, accent only) get defaults for the rest. */
export function parseTheme(value: unknown): Theme {
  if (!value || typeof value !== "object") throw new Error("Invalid theme file.");
  const theme = value as Record<string, unknown>;
  if (typeof theme.name !== "string" || !theme.name.trim() || theme.name.length > 80 ||
      typeof theme.font !== "string" || !Object.hasOwn(fonts, theme.font) ||
      typeof theme.size !== "number" || !Number.isInteger(theme.size) || theme.size < 12 || theme.size > 22 ||
      typeof theme.accent !== "string" || !hex.test(theme.accent)) throw new Error("Invalid theme: check its name, font, size and accent color.");
  const pick = <T extends string>(key: string, allowed: readonly string[], fallback: T): T => {
    const v = theme[key];
    if (v === undefined) return fallback;
    if (typeof v !== "string" || !allowed.includes(v)) throw new Error(`Invalid theme: unknown ${key} "${String(v)}".`);
    return v as T;
  };
  return {
    name: theme.name.trim(), font: theme.font, size: theme.size, accent: theme.accent,
    codeFont: pick("codeFont", Object.keys(codeFonts), defaultTheme.codeFont),
    mode: pick<Mode>("mode", modes, "system"),
    density: pick("density", Object.keys(densities), defaultTheme.density),
    radius: pick("radius", Object.keys(radii), defaultTheme.radius),
    light: parsePalette(theme.light), dark: parsePalette(theme.dark),
  };
}

function prefersDark() {
  try { return typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia("(prefers-color-scheme: dark)").matches; }
  catch { return false; }
}
export function effectiveScheme(theme: Pick<Theme, "mode">): Scheme {
  return theme.mode === "system" ? (prefersDark() ? "dark" : "light") : theme.mode;
}
/** The colour a theme shows for a role in a scheme: its own override, else the built-in palette. */
export function themeColor(theme: Theme, scheme: Scheme, key: ColorKey) { return theme[scheme][key] ?? basePalettes[scheme][key]; }
export function readableOn(color: string) {
  const rgb = [1, 3, 5].map(i => parseInt(color.slice(i, i + 2), 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4);
  return .2126 * rgb[0] + .7152 * rgb[1] + .0722 * rgb[2] > .179 ? "#000000" : "#ffffff";
}

let current: Theme | undefined;
let listening = false;
export function applyTheme(theme: Theme) {
  current = theme;
  const root = document.documentElement;
  const scheme = effectiveScheme(theme);
  root.classList.toggle("dark", scheme === "dark");
  root.classList.toggle("light", scheme === "light");
  root.style.colorScheme = scheme;
  root.style.setProperty("--appearance-font", fonts[theme.font]);
  root.style.setProperty("--appearance-code-font", codeFonts[theme.codeFont].family);
  root.style.fontSize = `${theme.size}px`;
  root.style.setProperty("--spacing", densities[theme.density].spacing);
  root.style.setProperty("--radius", radii[theme.radius].value);
  for (const name of ["--primary", "--ring", "--sidebar-primary", "--sidebar-ring"]) root.style.setProperty(name, theme.accent);
  const foreground = readableOn(theme.accent);
  root.style.setProperty("--primary-foreground", foreground);
  root.style.setProperty("--sidebar-primary-foreground", foreground);
  const palette = theme[scheme];
  for (const key of colorKeys) for (const name of colorVars[key]) {
    const color = palette[key];
    if (color) root.style.setProperty(name, color); else root.style.removeProperty(name);
  }
  // Follow the operating system when it switches between light and dark.
  if (!listening && typeof window !== "undefined" && typeof window.matchMedia === "function") {
    listening = true;
    try { window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => { if (current?.mode === "system") applyTheme(current); }); }
    catch { /* no media query support */ }
  }
}
export function loadActiveTheme(): Theme {
  try { const raw = localStorage.getItem(activeKey); return raw ? parseTheme(JSON.parse(raw)) : defaultTheme; }
  catch { return defaultTheme; }
}
export function saveActiveTheme(theme: Theme) { localStorage.setItem(activeKey, JSON.stringify(theme)); }
export function loadThemes(): Theme[] {
  try {
    const raw = localStorage.getItem(themesKey); const values: unknown = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(values)) return [];
    return values.flatMap(value => { try { return [parseTheme(value)]; } catch { return []; } });
  }
  catch { return []; }
}
export function saveThemes(themes: Theme[]) { localStorage.setItem(themesKey, JSON.stringify(themes)); }
export function sameTheme(a: Theme, b: Theme) {
  const keys = ["name", "font", "codeFont", "size", "accent", "mode", "density", "radius"] as const;
  return keys.every(key => String(a[key]).trim().toLowerCase() === String(b[key]).trim().toLowerCase()) &&
    (["light", "dark"] as const).every(scheme => colorKeys.every(key => a[scheme][key]?.toLowerCase() === b[scheme][key]?.toLowerCase()));
}
