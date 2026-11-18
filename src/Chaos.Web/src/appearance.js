const scenes = import.meta.glob("../../Chaos.Desktop/frontend/src/assets/day-backgrounds/*.svg", { eager: true, query: "?url", import: "default" });

export function dayBackgroundStyle(raw) {
  try {
    const background = JSON.parse(raw);
    if (background?.kind === "color" && /^#[0-9a-f]{6}$/i.test(background.value)) return { backgroundColor: background.value };
    if (background?.kind === "scene" && /^[a-z]+$/.test(background.value)) {
      const url = scenes[`../../Chaos.Desktop/frontend/src/assets/day-backgrounds/${background.value}.svg`];
      if (url) return { backgroundImage: `url("${url}")` };
    }
  } catch { return undefined; }
  return undefined;
}
