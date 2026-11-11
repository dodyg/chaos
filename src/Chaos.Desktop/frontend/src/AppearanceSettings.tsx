import React from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field } from "@/components/common";
import { applyTheme, defaultTheme, loadActiveTheme, loadThemes, parseTheme, saveActiveTheme, saveThemes, type Theme } from "./appearance";

export function AppearanceSettings() {
  const [theme, setTheme] = React.useState(loadActiveTheme);
  const [themes, setThemes] = React.useState(loadThemes);
  const [selected, setSelected] = React.useState("");
  const [message, setMessage] = React.useState("");
  function update(next: Theme) {
    setTheme(next); applyTheme(next);
    try { saveActiveTheme(next); setMessage("Appearance saved on this computer."); }
    catch { setMessage("Appearance is applied, but could not be saved on this computer."); }
  }
  function store(next: Theme[]) {
    try { saveThemes(next); setThemes(next); setMessage("Themes saved on this computer."); return true; }
    catch { setMessage("Could not save themes on this computer."); return false; }
  }
  function saveNamed() {
    try {
      const named = parseTheme(theme);
      if (store([...themes.filter(t => t.name.toLocaleLowerCase() !== named.name.toLocaleLowerCase()), named])) setSelected(named.name);
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
      if (store([...themes.filter(t => t.name.toLocaleLowerCase() !== imported.name.toLocaleLowerCase()), imported])) { setSelected(imported.name); update(imported); }
    } catch (error) { setMessage(error instanceof Error ? error.message : "Could not import theme."); }
  }
  return <section aria-label="Appearance" className="grid gap-3 rounded-xl border bg-card p-4">
    <div><h3 className="text-base font-semibold">Appearance</h3><p className="text-sm text-muted-foreground">Changes apply immediately and stay on this computer. Save named themes to switch between them or export them to share.</p></div>
    <div className="grid gap-3 sm:grid-cols-2">
      <Field label="Font"><NativeSelect value={theme.font} onChange={e => update({ ...theme, font: e.target.value })}>
        <NativeSelectOption value="system">System</NativeSelectOption><NativeSelectOption value="sans">Arial</NativeSelectOption><NativeSelectOption value="serif">Georgia</NativeSelectOption><NativeSelectOption value="mono">Consolas</NativeSelectOption>
      </NativeSelect></Field>
      <Field label="Font size"><NativeSelect value={String(theme.size)} onChange={e => update({ ...theme, size: Number(e.target.value) })}>{Array.from({ length: 11 }, (_, i) => i + 12).map(size => <NativeSelectOption key={size} value={String(size)}>{size} px</NativeSelectOption>)}</NativeSelect></Field>
      <Field label="Accent color"><Input type="color" className="h-10 w-20 p-1" value={theme.accent} onChange={e => update({ ...theme, accent: e.target.value })}/></Field>
      <Field label="Theme name"><Input value={theme.name} maxLength={80} onChange={e => update({ ...theme, name: e.target.value })}/></Field>
    </div>
    <div className="flex flex-wrap gap-2"><Button type="button" onClick={saveNamed}>Save named theme</Button><Button type="button" variant="outline" onClick={exportTheme}>Export theme</Button><Button type="button" variant="outline" onClick={() => update(defaultTheme)}>Reset appearance</Button></div>
    <div className="grid gap-3 sm:grid-cols-2"><Field label="Saved themes"><NativeSelect value={selected} onChange={e => { const name = e.target.value; setSelected(name); const found = themes.find(t => t.name === name); if (found) update(found); }}><NativeSelectOption value="">Choose a theme</NativeSelectOption>{themes.map(t => <NativeSelectOption key={t.name} value={t.name}>{t.name}</NativeSelectOption>)}</NativeSelect></Field>
      <Field label="Import theme"><Input type="file" accept=".json,application/json" onChange={e => { void importTheme(e.target.files?.[0]); e.target.value = ""; }}/></Field></div>
    <div><Button type="button" variant="outline" disabled={!selected} onClick={() => { if (store(themes.filter(t => t.name !== selected))) setSelected(""); }}>Delete saved theme</Button></div>
    {message && <p role="status" className="text-sm text-muted-foreground">{message}</p>}
  </section>;
}
