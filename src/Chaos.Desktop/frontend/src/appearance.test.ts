import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { applyTheme, defaultTheme, loadActiveTheme, loadThemes, parseTheme, presetThemes, sameTheme, saveActiveTheme, saveThemes, themeColor } from "./appearance";

test("named themes round-trip and apply their appearance", () => {
  const dom = new JSDOM("<!doctype html><html></html>", { url: "http://localhost" });
  Object.defineProperties(globalThis, { document: { value: dom.window.document, configurable: true }, localStorage: { value: dom.window.localStorage, configurable: true } });
  const theme = parseTheme({ name: "  Ocean  ", font: "serif", size: 20, accent: "#123456" });
  saveActiveTheme(theme); saveThemes([theme]);
  assert.deepEqual(loadActiveTheme(), theme);
  assert.deepEqual(loadThemes(), [theme]);
  applyTheme(loadActiveTheme());
  assert.equal(dom.window.document.documentElement.style.fontSize, "20px");
  assert.equal(dom.window.document.documentElement.style.getPropertyValue("--primary"), "#123456");
  assert.equal(dom.window.document.documentElement.style.getPropertyValue("--primary-foreground"), "#ffffff");
  assert.equal(theme.name, "Ocean");
  dom.window.localStorage.setItem("chaos.appearance.active.v1", "bad json");
  assert.deepEqual(loadActiveTheme(), defaultTheme);
  assert.throws(() => parseTheme({ ...theme, accent: "red; color: red" }));
  assert.throws(() => parseTheme({ ...theme, size: 100 }));
});

test("reading fonts can be saved and restored in themes", () => {
  const dom = new JSDOM("<!doctype html><html></html>", { url: "http://localhost" });
  Object.defineProperties(globalThis, { document: { value: dom.window.document, configurable: true }, localStorage: { value: dom.window.localStorage, configurable: true } });
  for (const [font, family] of [["calibri", "Calibri"], ["tahoma", "Tahoma"], ["trebuchet", "Trebuchet MS"], ["verdana", "Verdana"]]) {
    const theme = parseTheme({ ...defaultTheme, font });
    saveActiveTheme(theme);
    applyTheme(loadActiveTheme());
    assert.match(dom.window.document.documentElement.style.getPropertyValue("--appearance-font"), new RegExp(family));
    assert.equal(loadActiveTheme().font, font);
  }
});

test("older themes load with defaults and new options apply to the page", () => {
  const dom = new JSDOM("<!doctype html><html></html>", { url: "http://localhost" });
  Object.defineProperties(globalThis, { document: { value: dom.window.document, configurable: true }, localStorage: { value: dom.window.localStorage, configurable: true } });
  const old = parseTheme({ name: "Old", font: "system", size: 16, accent: "#123456" });
  assert.deepEqual(old, { ...defaultTheme, name: "Old", accent: "#123456" });
  const theme = parseTheme({ ...defaultTheme, name: "Night", mode: "dark", density: "compact", radius: "none", codeFont: "cascadia",
    dark: { border: "#ABCDEF", background: "#101010" }, light: { text: "#202020" } });
  assert.deepEqual(Object.keys(theme.dark), ["background", "border"]);
  applyTheme(theme);
  const root = dom.window.document.documentElement;
  assert.ok(root.classList.contains("dark"));
  assert.equal(root.style.colorScheme, "dark");
  assert.equal(root.style.getPropertyValue("--spacing"), "0.22rem");
  assert.equal(root.style.getPropertyValue("--radius"), "0rem");
  assert.match(root.style.getPropertyValue("--appearance-code-font"), /Cascadia Code/);
  assert.equal(root.style.getPropertyValue("--background"), "#101010");
  assert.equal(root.style.getPropertyValue("--input"), "#abcdef");
  assert.equal(root.style.getPropertyValue("--foreground"), "", "light-only colors stay off in dark mode");
  applyTheme({ ...theme, mode: "light" });
  assert.ok(root.classList.contains("light") && !root.classList.contains("dark"));
  assert.equal(root.style.getPropertyValue("--background"), "");
  assert.equal(root.style.getPropertyValue("--foreground"), "#202020");
  assert.throws(() => parseTheme({ ...theme, mode: "dim" }));
  assert.throws(() => parseTheme({ ...theme, density: "huge" }));
  assert.throws(() => parseTheme({ ...theme, dark: { background: "url(x)" } }));
  assert.throws(() => parseTheme({ ...theme, dark: { glow: "#000000" } }));
  saveThemes([theme, { name: "" } as never]);
  assert.deepEqual(loadThemes(), [theme], "a broken saved theme does not hide the others");
});

test("every built-in theme is valid, uniquely named and readable", () => {
  const names = new Set(presetThemes.map(t => t.name.toLowerCase()));
  assert.equal(names.size, presetThemes.length);
  assert.ok(presetThemes.length >= 10);
  const luminance = (color: string) => { const [r, g, b] = [1, 3, 5].map(i => parseInt(color.slice(i, i + 2), 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4); return .2126 * r + .7152 * g + .0722 * b; };
  const contrast = (a: string, b: string) => { const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p); return (x + .05) / (y + .05); };
  for (const preset of presetThemes) {
    assert.ok(sameTheme(parseTheme(preset), preset), preset.name);
    for (const scheme of preset.mode === "system" ? ["light", "dark"] as const : [preset.mode]) {
      const text = themeColor(preset, scheme, "text"), muted = themeColor(preset, scheme, "mutedText");
      for (const surface of ["background", "surface", "sidebar"] as const) {
        assert.ok(contrast(text, themeColor(preset, scheme, surface)) >= 7, `${preset.name} ${scheme} text on ${surface}`);
        assert.ok(contrast(muted, themeColor(preset, scheme, surface)) >= 4.5, `${preset.name} ${scheme} secondary text on ${surface}`);
      }
    }
  }
});
