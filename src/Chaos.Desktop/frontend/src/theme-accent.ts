export const defaultAccent = "#c45a20";

export function readableOn(color: string) {
  const rgb = [1, 3, 5].map(i => parseInt(color.slice(i, i + 2), 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4);
  return .2126 * rgb[0] + .7152 * rgb[1] + .0722 * rgb[2] > .179 ? "#000000" : "#ffffff";
}

export function applyAccent(root: HTMLElement, accent: string = defaultAccent) {
  for (const name of ["--primary", "--ring", "--sidebar-primary", "--sidebar-ring"]) root.style.setProperty(name, accent);
  const foreground = readableOn(accent);
  root.style.setProperty("--primary-foreground", foreground);
  root.style.setProperty("--sidebar-primary-foreground", foreground);
}
