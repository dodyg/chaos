import React from "react";
import { Check, Monitor, Moon, RotateCcw, Sun, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field } from "@/components/common";
import { cn } from "@/lib/utils";
import {
  applyTheme, codeFonts, colorKeys, colorLabels, defaultTheme, densities, effectiveScheme, fontNames, loadActiveTheme, loadThemes,
  parseTheme, presetThemes, radii, readableOn, sameTheme, saveActiveTheme, saveThemes, themeColor, type ColorKey, type Mode, type Scheme, type Theme,
} from "./appearance";

const accentColors = [
  { name: "Orange", value: "#c45a20" }, { name: "Coral", value: "#d04a4a" },
  { name: "Rose", value: "#be3567" }, { name: "Berry", value: "#a33b80" },
  { name: "Purple", value: "#8052b5" }, { name: "Violet", value: "#6657c7" },
  { name: "Indigo", value: "#495bc1" }, { name: "Blue", value: "#2674c7" },
  { name: "Sky", value: "#0086b5" }, { name: "Teal", value: "#00877f" },
  { name: "Emerald", value: "#16865b" }, { name: "Green", value: "#50852d" },
  { name: "Olive", value: "#78821f" }, { name: "Gold", value: "#a97708" },
  { name: "Copper", value: "#a75d30" }, { name: "Slate", value: "#607487" },
];
const modeOptions: { value: Mode; label: string; icon: React.ReactNode }[] = [
  { value: "system", label: "Match system", icon: <Monitor aria-hidden="true"/> },
  { value: "light", label: "Light", icon: <Sun aria-hidden="true"/> },
  { value: "dark", label: "Dark", icon: <Moon aria-hidden="true"/> },
];

/** Re-renders when the operating system switches between light and dark. */
function useSystemScheme() {
  const [, setTick] = React.useState(0);
  React.useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const query = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => setTick(t => t + 1);
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, []);
}

/** A miniature window drawn in the theme's own colours. */
function ThemePreview({ theme, scheme: shown }: { theme: Theme; scheme: Scheme }) {
  const scheme = theme.mode === "system" ? shown : theme.mode;
  const c = (key: ColorKey) => themeColor(theme, scheme, key);
  return <span aria-hidden="true" className="flex h-16 w-full overflow-hidden rounded-md border" style={{ backgroundColor: c("background"), borderColor: c("border") }}>
    <span className="flex w-1/4 flex-col gap-1 p-1.5" style={{ backgroundColor: c("sidebar"), borderRight: `1px solid ${c("border")}` }}>
      <span className="h-1.5 rounded-full" style={{ backgroundColor: theme.accent }}/>
      <span className="h-1.5 rounded-full opacity-50" style={{ backgroundColor: c("mutedText") }}/>
      <span className="h-1.5 rounded-full opacity-50" style={{ backgroundColor: c("mutedText") }}/>
    </span>
    <span className="flex flex-1 flex-col gap-1 p-1.5">
      <span className="h-2 w-2/3 rounded-full" style={{ backgroundColor: c("text") }}/>
      <span className="flex flex-1 flex-col justify-center gap-1 rounded-sm px-1.5" style={{ backgroundColor: c("surface"), border: `1px solid ${c("border")}` }}>
        <span className="h-1 w-4/5 rounded-full" style={{ backgroundColor: c("mutedText") }}/>
        <span className="h-1 w-3/5 rounded-full" style={{ backgroundColor: c("mutedText") }}/>
      </span>
      <span className="ml-auto h-2.5 w-8 rounded-full" style={{ backgroundColor: theme.accent }}/>
    </span>
  </span>;
}

/** `scheme` is the mode the app shows now; themes that follow the system preview in it. */
function ThemeCard({ theme, scheme, active, onPick, onDelete }: { theme: Theme; scheme: Scheme; active: boolean; onPick: () => void; onDelete?: () => void }) {
  const hint = theme.mode === "system" ? "Light and dark" : theme.mode === "dark" ? "Dark" : "Light";
  return <div className="group relative">
    <button type="button" aria-pressed={active} onClick={onPick} title={`${theme.name} (${hint})`}
      className={cn("grid w-full gap-1.5 rounded-lg border bg-background p-1.5 text-left outline-none transition-colors hover:border-foreground/30 focus-visible:ring-[3px] focus-visible:ring-ring/50",
        active && "border-primary ring-2 ring-primary/40 hover:border-primary")}>
      <ThemePreview theme={theme} scheme={scheme}/>
      <span className="flex min-w-0 items-center gap-1 px-0.5">
        <span className="min-w-0 flex-1 truncate text-xs font-medium">{theme.name}</span>
        {active ? <Check aria-hidden="true" className="size-3.5 shrink-0 text-primary"/> : <span className="shrink-0 text-[0.65rem] text-muted-foreground">{hint}</span>}
      </span>
    </button>
    {onDelete && <Button type="button" size="icon" variant="outline" aria-label={`Delete ${theme.name}`} title="Delete saved theme" onClick={onDelete}
      className="absolute top-1 right-1 size-6 bg-background/90 opacity-0 group-focus-within:opacity-100 group-hover:opacity-100 [&_svg]:size-3.5"><Trash2/></Button>}
  </div>;
}

function Segmented<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: { value: T; label: string; icon?: React.ReactNode }[]; onChange: (value: T) => void }) {
  return <fieldset className="grid content-start gap-1.5">
    <legend className="mb-1.5 text-xs font-medium text-muted-foreground">{label}</legend>
    <div role="radiogroup" aria-label={label} className="flex flex-wrap gap-1 rounded-lg border bg-muted/50 p-1">
      {options.map(option => <label key={option.value} className={cn("relative flex flex-1 cursor-pointer items-center justify-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs whitespace-nowrap text-muted-foreground transition-colors hover:text-foreground has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring/50 [&_svg]:size-3.5",
        value === option.value && "bg-background font-medium text-foreground shadow-xs")}>
        <input type="radio" className="sr-only" name={label} value={option.value} checked={value === option.value} onChange={() => onChange(option.value)}/>
        {option.icon}{option.label}
      </label>)}
    </div>
  </fieldset>;
}

function ColorField({ label, value, overridden, onChange, onReset }: { label: string; value: string; overridden: boolean; onChange: (value: string) => void; onReset: () => void }) {
  return <div className="flex min-w-0 items-center gap-2 rounded-lg border bg-background p-1.5">
    <label className="flex min-w-0 flex-1 cursor-pointer items-center gap-2">
      <span className="relative size-7 shrink-0 overflow-hidden rounded-md border border-foreground/20 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring/50" style={{ backgroundColor: value }}>
        <input type="color" value={value} onChange={e => onChange(e.target.value)} aria-label={label} className="absolute inset-0 size-full cursor-pointer opacity-0"/>
      </span>
      <span className="grid min-w-0">
        <span className="truncate text-xs font-medium">{label}</span>
        <span className="font-mono text-[0.65rem] text-muted-foreground uppercase">{overridden ? value : "Default"}</span>
      </span>
    </label>
    {overridden && <Button type="button" size="icon" variant="ghost" aria-label={`Reset ${label}`} title="Use the default color" onClick={onReset} className="size-7 [&_svg]:size-3.5"><RotateCcw/></Button>}
  </div>;
}

export function AppearanceSettings() {
  useSystemScheme();
  const [theme, setTheme] = React.useState(loadActiveTheme);
  const [themes, setThemes] = React.useState(loadThemes);
  const [editing, setEditing] = React.useState<Scheme>(() => effectiveScheme(loadActiveTheme()));
  const [message, setMessage] = React.useState("");
  const scheme = effectiveScheme(theme);
  /** `pick` is set when a whole theme is chosen, which also moves the colour editor to the mode it shows. */
  function update(next: Theme, pick = false) {
    setTheme(next); applyTheme(next);
    if (pick || next.mode !== theme.mode) setEditing(effectiveScheme(next));
    try { saveActiveTheme(next); setMessage("Appearance saved on this computer."); }
    catch { setMessage("Appearance is applied, but could not be saved on this computer."); }
  }
  const set = <K extends keyof Theme>(key: K, value: Theme[K]) => update({ ...theme, [key]: value });
  function setColor(key: ColorKey, value: string | undefined) {
    const palette = { ...theme[editing] };
    if (value) palette[key] = value.toLowerCase(); else delete palette[key];
    update({ ...theme, [editing]: palette });
  }
  function store(next: Theme[]) {
    try { saveThemes(next); setThemes(next); setMessage("Themes saved on this computer."); return true; }
    catch { setMessage("Could not save themes on this computer."); return false; }
  }
  function saveNamed() {
    try {
      const named = parseTheme(theme);
      if (presetThemes.some(p => p.name.toLocaleLowerCase() === named.name.toLocaleLowerCase()) && !sameTheme(named, presetThemes.find(p => p.name.toLocaleLowerCase() === named.name.toLocaleLowerCase())!)) {
        setMessage(`"${named.name}" is a built-in theme. Give your version a new name to save it.`); return;
      }
      store([...themes.filter(t => t.name.toLocaleLowerCase() !== named.name.toLocaleLowerCase()), named]);
    } catch (error) { setMessage(error instanceof Error ? error.message : "Invalid theme."); }
  }
  function exportTheme() {
    try {
      const named = parseTheme(theme);
      const url = URL.createObjectURL(new Blob([JSON.stringify(named, null, 2)], { type: "application/json" }));
      const link = document.createElement("a"); link.href = url; link.download = `${named.name.replace(/[\\/:*?"<>|\x00-\x1f]/g, "-")}.json`; link.click(); URL.revokeObjectURL(url);
    } catch (error) { setMessage(error instanceof Error ? error.message : "Could not export theme."); }
  }
  async function importTheme(file: File | undefined) {
    if (!file) return;
    try {
      if (file.size > 10000) throw new Error("Theme file is too large.");
      const imported = parseTheme(JSON.parse(await file.text()));
      if (store([...themes.filter(t => t.name.toLocaleLowerCase() !== imported.name.toLocaleLowerCase()), imported])) update(imported, true);
    } catch (error) { setMessage(error instanceof Error ? error.message : "Could not import theme."); }
  }
  const customAccent = !accentColors.some(color => color.value === theme.accent.toLowerCase());
  const overrides = colorKeys.filter(key => theme[editing][key]).length;

  return <div className="grid gap-4">
    <section aria-label="Themes" className="grid gap-3 rounded-xl border bg-card p-4">
      <div><h3 className="text-base font-semibold">Themes</h3><p className="text-sm text-muted-foreground">Pick a ready-made look, then fine-tune it below. Changes apply immediately and stay on this computer.</p></div>
      <div className="grid grid-cols-[repeat(auto-fill,minmax(8.5rem,1fr))] gap-2">
        {presetThemes.map(preset => <ThemeCard key={preset.name} theme={preset} scheme={scheme} active={sameTheme(theme, preset)} onPick={() => update(preset, true)}/>)}
      </div>
      {themes.length > 0 && <>
        <h4 className="mt-1 text-sm font-semibold">Your themes</h4>
        <div className="grid grid-cols-[repeat(auto-fill,minmax(8.5rem,1fr))] gap-2">
          {themes.map(saved => <ThemeCard key={saved.name} theme={saved} scheme={scheme} active={sameTheme(theme, saved)} onPick={() => update(saved, true)}
            onDelete={() => store(themes.filter(t => t.name !== saved.name))}/>)}
        </div>
      </>}
    </section>

    <section aria-label="Appearance" className="grid gap-4 rounded-xl border bg-card p-4">
      <div><h3 className="text-base font-semibold">Customize</h3><p className="text-sm text-muted-foreground">Adjust any part of the current theme. Save it under a name to keep it, or export it to share.</p></div>
      <div className="grid gap-3 sm:grid-cols-2">
        <Segmented label="Mode" value={theme.mode} options={modeOptions} onChange={mode => set("mode", mode)}/>
        <Segmented label="Density" value={theme.density} options={Object.entries(densities).map(([value, d]) => ({ value, label: d.label }))} onChange={density => set("density", density)}/>
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        <Field label="Font"><NativeSelect value={theme.font} onChange={e => set("font", e.target.value)}>
          {Object.entries(fontNames).map(([value, label]) => <NativeSelectOption key={value} value={value}>{label}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Field label="Code font"><NativeSelect value={theme.codeFont} onChange={e => set("codeFont", e.target.value)}>
          {Object.entries(codeFonts).map(([value, f]) => <NativeSelectOption key={value} value={value}>{f.label}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Field label="Font size"><NativeSelect value={String(theme.size)} onChange={e => set("size", Number(e.target.value))}>{Array.from({ length: 11 }, (_, i) => i + 12).map(size => <NativeSelectOption key={size} value={String(size)}>{size} px</NativeSelectOption>)}</NativeSelect></Field>
      </div>
      <Segmented label="Corners" value={theme.radius} options={Object.entries(radii).map(([value, r]) => ({ value, label: r.label,
        icon: <span aria-hidden="true" className="size-3.5 border-2 border-current border-r-0 border-b-0" style={{ borderTopLeftRadius: `calc(${r.value} * 0.8)` }}/> }))} onChange={radius => set("radius", radius)}/>

      <fieldset className="grid content-start gap-2">
        <legend className="mb-1.5 text-xs font-medium text-muted-foreground">Accent color</legend>
        <div className="flex flex-wrap items-center gap-2" role="radiogroup" aria-label="Accent color">
          {accentColors.map(color => {
            const checked = theme.accent.toLowerCase() === color.value;
            return <label key={color.value} className="relative cursor-pointer" title={color.name}>
              <input type="radio" name="accent-color" value={color.value} checked={checked} onChange={() => set("accent", color.value)} className="peer sr-only" aria-label={color.name}/>
              <span className="flex size-8 items-center justify-center rounded-full border border-foreground/30 peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-ring" style={{ backgroundColor: color.value }}>
                {checked && <Check aria-hidden="true" className="size-4 text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.8)]"/>}
              </span>
            </label>;
          })}
          <label className="relative cursor-pointer" title="Custom accent color">
            <span className={cn("relative flex size-8 items-center justify-center overflow-hidden rounded-full border border-foreground/30 has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-ring",
              !customAccent && "bg-[conic-gradient(#d04a4a,#a97708,#16865b,#0086b5,#6657c7,#be3567,#d04a4a)]")} style={customAccent ? { backgroundColor: theme.accent } : undefined}>
              <input type="color" value={theme.accent} onChange={e => set("accent", e.target.value.toLowerCase())} aria-label="Custom accent color" className="absolute inset-0 size-full cursor-pointer opacity-0"/>
              {customAccent && <Check aria-hidden="true" className="size-4" style={{ color: readableOn(theme.accent) }}/>}
            </span>
          </label>
        </div>
      </fieldset>

      <fieldset className="grid gap-2">
        <legend className="mb-1.5 text-xs font-medium text-muted-foreground">Colors</legend>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <Segmented label="Edit colors for" value={editing} onChange={setEditing}
            options={[{ value: "light", label: "Light mode", icon: <Sun aria-hidden="true"/> }, { value: "dark", label: "Dark mode", icon: <Moon aria-hidden="true"/> }]}/>
          {overrides > 0 && <Button type="button" size="sm" variant="outline" onClick={() => update({ ...theme, [editing]: {} })}><RotateCcw/>Reset {editing} colors</Button>}
        </div>
        {editing !== scheme && <p className="text-xs text-muted-foreground">You are editing the {editing} colors while the app shows {scheme} mode{theme.mode === "system" ? "" : " (pinned by this theme)"}. Previews update when that mode is on.</p>}
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {colorKeys.map(key => <ColorField key={key} label={colorLabels[key]} value={themeColor(theme, editing, key)} overridden={!!theme[editing][key]}
            onChange={value => setColor(key, value)} onReset={() => setColor(key, undefined)}/>)}
        </div>
      </fieldset>

      <div className="grid gap-3 border-t pt-4 sm:grid-cols-2">
        <Field label="Theme name"><Input value={theme.name} maxLength={80} onChange={e => set("name", e.target.value)}/></Field>
        <Field label="Import theme"><Input type="file" accept=".json,application/json" onChange={e => { void importTheme(e.target.files?.[0]); e.target.value = ""; }}/></Field>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button type="button" onClick={saveNamed}>Save named theme</Button>
        <Button type="button" variant="outline" onClick={exportTheme}>Export theme</Button>
        <Button type="button" variant="outline" onClick={() => update(defaultTheme, true)}>Reset appearance</Button>
      </div>
      {message && <p role="status" className="text-sm text-muted-foreground">{message}</p>}
    </section>
  </div>;
}
