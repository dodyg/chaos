import React from "react";
import { shellRpc } from "#neoastra";
import { applyTheme, parseTheme } from "./appearance";

const themeKey = "chaos.appearance.active.v1";
const backgroundKey = "chaos.myday.background.v1";
const scenes = new Set(["sunset", "tide", "botanical", "constellation", "dunes", "orbit", "aurora", "pines", "hills", "lake", "lavender", "snowpeaks", "skyline", "bamboo", "clouds", "autumn", "canyon", "lighthouse"]);
function validBackground(value: unknown): value is { kind: string; value?: string } {
  if (!value || typeof value !== "object" || !("kind" in value)) return false;
  const b = value as { kind: string; value?: unknown };
  return b.kind === "default" || (b.kind === "color" && typeof b.value === "string" && /^#[0-9a-f]{6}$/i.test(b.value)) || (b.kind === "scene" && typeof b.value === "string" && scenes.has(b.value));
}

/** Keep existing local choices until the initial account pull has finished. */
export function useAppearanceSync() {
  React.useEffect(() => {
    let stopped = false;
    let generation = 0;
    let refreshing = false;
    const pendingKey = "chaos.appearance.pending.v1";
    let pending: Record<string, string> = {};
    try { pending = JSON.parse(localStorage.getItem(pendingKey) ?? "{}"); } catch { pending = {}; }
    const persistPending = () => localStorage.setItem(pendingKey, JSON.stringify(pending));
    const onLocal = (event: Event) => {
      const { kind, value } = (event as CustomEvent<{ kind: string; value: unknown }>).detail;
      generation++;
      pending[kind] = JSON.stringify(value);
      persistPending();
      void refresh();
    };
    window.addEventListener("chaos-appearance-local", onLocal);
    const refresh = async () => {
      if (refreshing) return;
      refreshing = true;
      try {
        for (const [kind, value] of Object.entries(pending)) {
          const result = await shellRpc.saveAppearance({ kind, value, onlyIfEmpty: false });
          if (!result.saved) throw new Error("Appearance could not be saved.");
          if (pending[kind] === value) { delete pending[kind]; persistPending(); }
        }
        if (Object.keys(pending).length) return;
        const current = generation;
        const result = await shellRpc.appearance({});
        if (stopped || current !== generation || !result.ready) return;
        for (const [kind, key, raw] of [["theme", themeKey, result.theme], ["background", backgroundKey, result.background]] as const) {
          if (current !== generation) return;
          if (!raw) {
            const local = localStorage.getItem(key);
            if (local) {
              const parsed: unknown = JSON.parse(local);
              if (kind === "theme") parseTheme(parsed); else if (!validBackground(parsed)) continue;
              await shellRpc.saveAppearance({ kind, value: local, onlyIfEmpty: true });
            }
            continue;
          }
          const parsed: unknown = JSON.parse(raw);
          if (kind === "theme") { const theme = parseTheme(parsed); localStorage.setItem(key, JSON.stringify(theme)); applyTheme(theme); }
          else if (validBackground(parsed)) {
            const local = localStorage.getItem(key);
            if (local && JSON.parse(local).kind === "image") continue;
            localStorage.setItem(key, JSON.stringify(parsed));
          }
        }
        window.dispatchEvent(new window.Event("chaos-appearance-remote"));
      } catch (error) { console.error("Could not refresh appearance", error); }
      finally { refreshing = false; }
    };
    void refresh();
    const timer = window.setInterval(() => void refresh(), 10000);
    return () => { stopped = true; window.clearInterval(timer); window.removeEventListener("chaos-appearance-local", onLocal); };
  }, []);
}
