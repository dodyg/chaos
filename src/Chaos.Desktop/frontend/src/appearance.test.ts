import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { applyTheme, defaultTheme, loadActiveTheme, loadThemes, parseTheme, saveActiveTheme, saveThemes } from "./appearance";

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
