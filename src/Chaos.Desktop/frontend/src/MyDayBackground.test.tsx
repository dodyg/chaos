import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  FileReader: { value: dom.window.FileReader, configurable: true },
  localStorage: { value: dom.window.localStorage, configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const { DayBackgroundSettings, backgroundScenes, loadDayBackground, useDayBackground } = await import("./MyDayBackground");

function Settings() {
  const { background, save } = useDayBackground();
  return <DayBackgroundSettings background={background} onSave={save}/>;
}

test("background color and image persist locally and reset to default", async () => {
  dom.window.localStorage.clear();
  render(<Settings/>);
  fireEvent.click(screen.getByLabelText("Ocean"));
  assert.deepEqual(loadDayBackground(), { kind: "color", value: "#245e77" });
  fireEvent.click(screen.getByLabelText("Mountain sunset"));
  assert.deepEqual(loadDayBackground(), { kind: "scene", value: "sunset" });
  assert.equal(backgroundScenes.length, 6);
  const image = new dom.window.File(["test image"], "background.png", { type: "image/png" });
  fireEvent.change(screen.getByLabelText("Background image"), { target: { files: [image] } });
  await waitFor(() => assert.equal(loadDayBackground().kind, "image"));
  fireEvent.click(screen.getByRole("button", { name: "Reset background", hidden: true }));
  assert.deepEqual(loadDayBackground(), { kind: "default" });
  cleanup();
});

test("rejects oversized images without replacing the current background", () => {
  dom.window.localStorage.clear();
  render(<Settings/>);
  fireEvent.click(screen.getByLabelText("Ocean"));
  const image = new dom.window.File([new Uint8Array(2 * 1024 * 1024 + 1)], "large.png", { type: "image/png" });
  fireEvent.change(screen.getByLabelText("Background image"), { target: { files: [image] } });
  assert.match(screen.getByRole("alert").textContent ?? "", /2 MB/);
  assert.deepEqual(loadDayBackground(), { kind: "color", value: "#245e77" });
  cleanup();
});

test("previous custom colors are preserved and unknown scenes are rejected", () => {
  dom.window.localStorage.setItem("chaos.myday.background.v1", JSON.stringify({ kind: "color", value: "#123456" }));
  assert.deepEqual(loadDayBackground(), { kind: "color", value: "#123456" });
  dom.window.localStorage.setItem("chaos.myday.background.v1", JSON.stringify({ kind: "scene", value: "unknown" }));
  assert.deepEqual(loadDayBackground(), { kind: "default" });
});
