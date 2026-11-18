const scenes = import.meta.glob("../../Chaos.Desktop/frontend/src/assets/day-backgrounds/*.svg", { eager: true, query: "?url&no-inline", import: "default" });

export function sceneBackgroundStyle(name) {
  if (typeof name !== "string" || !/^[a-z]+$/i.test(name)) return undefined;
  const url = scenes[`../../Chaos.Desktop/frontend/src/assets/day-backgrounds/${name.toLowerCase()}.svg`];
  return url ? { backgroundImage: `url("${url}")` } : undefined;
}

export function dayBackgroundStyle(raw) {
  try {
    const background = JSON.parse(raw);
    if (background?.kind === "color" && /^#[0-9a-f]{6}$/i.test(background.value)) return { backgroundColor: background.value };
    if (background?.kind === "scene" && /^[a-z]+$/.test(background.value)) {
      return sceneBackgroundStyle(background.value);
    }
  } catch { return undefined; }
  return undefined;
}
