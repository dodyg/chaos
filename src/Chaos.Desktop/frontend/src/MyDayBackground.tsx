import React from "react";
import { Check, ImagePlus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

export const backgroundColors = [
  { name: "Midnight", value: "#26354f" }, { name: "Ocean", value: "#245e77" }, { name: "Teal", value: "#28736e" }, { name: "Forest", value: "#3d644f" },
  { name: "Olive", value: "#697344" }, { name: "Sage", value: "#84977b" }, { name: "Sky", value: "#84adbf" }, { name: "Periwinkle", value: "#8d9ac7" },
  { name: "Indigo", value: "#4c5184" }, { name: "Plum", value: "#724b6e" }, { name: "Berry", value: "#934e68" }, { name: "Terracotta", value: "#ad6756" },
  { name: "Amber", value: "#b47b40" }, { name: "Sand", value: "#c7ad85" }, { name: "Rose", value: "#c39498" }, { name: "Slate", value: "#6c7986" },
];
export const backgroundScenes = [
  { id: "sunset", name: "Mountain sunset", url: new URL("./assets/day-backgrounds/sunset.svg", import.meta.url).href },
  { id: "tide", name: "Ocean tide", url: new URL("./assets/day-backgrounds/tide.svg", import.meta.url).href },
  { id: "botanical", name: "Botanical", url: new URL("./assets/day-backgrounds/botanical.svg", import.meta.url).href },
  { id: "constellation", name: "Night sky", url: new URL("./assets/day-backgrounds/constellation.svg", import.meta.url).href },
  { id: "dunes", name: "Desert dunes", url: new URL("./assets/day-backgrounds/dunes.svg", import.meta.url).href },
  { id: "orbit", name: "Orbit", url: new URL("./assets/day-backgrounds/orbit.svg", import.meta.url).href },
  { id: "aurora", name: "Northern lights", url: new URL("./assets/day-backgrounds/aurora.svg", import.meta.url).href },
  { id: "pines", name: "Misty pines", url: new URL("./assets/day-backgrounds/pines.svg", import.meta.url).href },
  { id: "hills", name: "Rolling hills", url: new URL("./assets/day-backgrounds/hills.svg", import.meta.url).href },
  { id: "lake", name: "Mountain lake", url: new URL("./assets/day-backgrounds/lake.svg", import.meta.url).href },
  { id: "lavender", name: "Lavender field", url: new URL("./assets/day-backgrounds/lavender.svg", import.meta.url).href },
  { id: "snowpeaks", name: "Snowy peaks", url: new URL("./assets/day-backgrounds/snowpeaks.svg", import.meta.url).href },
  { id: "skyline", name: "City dusk", url: new URL("./assets/day-backgrounds/skyline.svg", import.meta.url).href },
  { id: "bamboo", name: "Bamboo grove", url: new URL("./assets/day-backgrounds/bamboo.svg", import.meta.url).href },
  { id: "clouds", name: "Cloudscape", url: new URL("./assets/day-backgrounds/clouds.svg", import.meta.url).href },
  { id: "autumn", name: "Autumn leaves", url: new URL("./assets/day-backgrounds/autumn.svg", import.meta.url).href },
  { id: "canyon", name: "Red canyon", url: new URL("./assets/day-backgrounds/canyon.svg", import.meta.url).href },
  { id: "lighthouse", name: "Lighthouse", url: new URL("./assets/day-backgrounds/lighthouse.svg", import.meta.url).href },
];
export type DayBackground = { kind: "default" } | { kind: "color"; value: string } | { kind: "image"; value: string } | { kind: "scene"; value: string };
export function backgroundImage(background: DayBackground) {
  return background.kind === "image" ? background.value : background.kind === "scene" ? backgroundScenes.find(scene => scene.id === background.value)?.url : undefined;
}
const key = "chaos.myday.background.v1";
const defaultBackground: DayBackground = { kind: "default" };
const maxImageBytes = 2 * 1024 * 1024;

export function loadDayBackground(): DayBackground {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(key) ?? "null");
    if (value && typeof value === "object" && "kind" in value && "value" in value && typeof value.value === "string") {
      if (value.kind === "color" && /^#[0-9a-f]{6}$/i.test(value.value)) return { kind: "color", value: value.value };
      if (value.kind === "image" && value.value.length <= Math.ceil(maxImageBytes * 4 / 3) + 100 && /^data:image\/(png|jpeg|webp);base64,[a-z0-9+/=]+$/i.test(value.value)) return { kind: "image", value: value.value };
      if (value.kind === "scene" && backgroundScenes.some(scene => scene.id === value.value)) return { kind: "scene", value: value.value };
    }
  } catch { /* Use the default if storage is unavailable or invalid. */ }
  return defaultBackground;
}

export function useDayBackground() {
  const [background, setBackground] = React.useState<DayBackground>(loadDayBackground);
  function save(next: DayBackground) {
    try {
      if (next.kind === "default") localStorage.removeItem(key);
      else localStorage.setItem(key, JSON.stringify(next));
      setBackground(next);
      return true;
    } catch { return false; }
  }
  return { background, save };
}

/** The background chooser panel; My Day opens it from a small header button, so it takes no room until asked for. */
export function DayBackgroundSettings({ background, onSave, onClose }: { background: DayBackground; onSave: (background: DayBackground) => boolean; onClose?: () => void }) {
  const [error, setError] = React.useState("");
  const selection = React.useRef(0);
  function save(next: DayBackground) {
    setError(onSave(next) ? "" : "Could not save the background on this computer. Try a smaller image.");
  }
  function chooseImage(file: File | undefined) {
    const current = ++selection.current;
    if (!file) return;
    if (!/^image\/(png|jpeg|webp)$/.test(file.type)) { setError("Choose a PNG, JPEG, or WebP image."); return; }
    if (file.size > maxImageBytes) { setError("Choose an image no larger than 2 MB."); return; }
    const reader = new FileReader();
    reader.onload = () => { if (current === selection.current && typeof reader.result === "string") save({ kind: "image", value: reader.result }); };
    reader.onerror = () => { if (current === selection.current) setError("Could not read this image."); };
    reader.readAsDataURL(file);
  }
  return <section id="day-background-settings" aria-label="My Day background" className="rounded-xl border bg-card p-3">
    <div className="flex items-center gap-2 text-sm font-medium"><ImagePlus className="size-4"/>My Day background
      {onClose && <Button type="button" variant="ghost" size="icon-xs" className="ml-auto text-muted-foreground" aria-label="Close background settings" onClick={onClose}><X/></Button>}</div>
    <div className="grid gap-3 pt-3 text-sm">
      <p className="text-muted-foreground">Choose a color, a scene, or an image from this computer. Your choice stays on this computer.</p>
      <fieldset className="grid gap-2"><legend className="font-medium">Colors</legend>
        <div className="flex flex-wrap gap-2">{backgroundColors.map(color => <label key={color.value} className="relative cursor-pointer" title={color.name}>
          <input type="radio" name="day-background" className="peer sr-only" aria-label={color.name} checked={background.kind === "color" && background.value.toLowerCase() === color.value} onChange={() => { selection.current++; save({ kind: "color", value: color.value }); }}/>
          <span className="flex size-9 items-center justify-center rounded-full border border-foreground/30 peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-ring" style={{ backgroundColor: color.value }}>{background.kind === "color" && background.value.toLowerCase() === color.value && <Check aria-hidden="true" className="size-5 text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.8)]"/>}</span>
        </label>)}</div>
        {background.kind === "color" && !backgroundColors.some(color => color.value === background.value.toLowerCase()) && <p className="text-xs text-muted-foreground">Your previously chosen custom color is active.</p>}
      </fieldset>
      <fieldset className="grid gap-2"><legend className="font-medium">Scenes</legend>
        <div className="grid grid-cols-[repeat(auto-fill,minmax(7rem,1fr))] gap-2">{backgroundScenes.map(scene => <label key={scene.id} className="cursor-pointer" title={scene.name}>
          <input type="radio" name="day-background" className="peer sr-only" aria-label={scene.name} checked={background.kind === "scene" && background.value === scene.id} onChange={() => { selection.current++; save({ kind: "scene", value: scene.id }); }}/>
          <span className="relative block overflow-hidden rounded-md border border-border peer-checked:ring-2 peer-checked:ring-primary peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-ring"><img src={scene.url} alt="" className="aspect-[3/2] w-full object-cover"/>{background.kind === "scene" && background.value === scene.id && <Check aria-hidden="true" className="absolute right-1 top-1 size-5 rounded-full bg-black/60 p-0.5 text-white"/>}</span>
          <span className="mt-1 block text-xs text-muted-foreground">{scene.name}</span>
        </label>)}</div>
      </fieldset>
      <div className="flex flex-wrap items-end gap-3">
        <label className="grid max-w-72 gap-1">Background image<Input aria-label="Background image" type="file" accept="image/png,image/jpeg,image/webp" onChange={event => { chooseImage(event.target.files?.[0]); event.target.value = ""; }}/></label>
        <Button type="button" variant="outline" onClick={() => { selection.current++; save(defaultBackground); }}>Reset background</Button>
      </div>
      <p className="text-xs text-muted-foreground">PNG, JPEG or WebP, up to 2 MB.</p>
      {error && <p role="alert" className="text-destructive">{error}</p>}
    </div>
  </section>;
}
