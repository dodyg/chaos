import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost", pretendToBeVisual: true });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
});
const { render, screen, waitFor, cleanup, act } = await import("@testing-library/react");
const { fakeActivity } = await import("./activityTestApi");
const { configureActivity } = await import("./activity");
const { useRpcQuery } = await import("./useRpcQuery");

function Probe({ load, kinds = [] }: { load: () => Promise<string>; kinds?: string[] }) {
  const { data, error, reload } = useRpcQuery(load, [], kinds);
  return (
    <div>
      <p data-testid="data">{data ?? "none"}</p>
      <p data-testid="error">{error ?? ""}</p>
      <button onClick={() => void reload()}>reload</button>
    </div>
  );
}

test("a slow response that a newer load overtook does not overwrite the newer one", async () => {
  const feed = fakeActivity();
  configureActivity(feed.api);
  const gates: Array<(value: string) => void> = [];
  const load = () =>
    new Promise<string>((resolve) => {
      gates.push(resolve);
    });
  try {
    render(<Probe load={load} />);
    await waitFor(() => assert.equal(gates.length, 1));
    act(() => {
      screen.getByText("reload").click();
    });
    await waitFor(() => assert.equal(gates.length, 2));
    await act(async () => {
      gates[1]("fresh");
    });
    await waitFor(() => assert.equal(screen.getByTestId("data").textContent, "fresh"));
    await act(async () => {
      gates[0]("stale");
    });
    assert.equal(screen.getByTestId("data").textContent, "fresh");
  } finally {
    cleanup();
    configureActivity();
  }
});

test("a failed load reports its message and keeps the previous data", async () => {
  const feed = fakeActivity();
  configureActivity(feed.api);
  let calls = 0;
  const load = async () => {
    if (++calls > 1) throw new Error("boom");
    return "first";
  };
  try {
    render(<Probe load={load} />);
    await waitFor(() => assert.equal(screen.getByTestId("data").textContent, "first"));
    act(() => {
      screen.getByText("reload").click();
    });
    await waitFor(() => assert.equal(screen.getByTestId("error").textContent, "boom"));
    assert.equal(screen.getByTestId("data").textContent, "first");
  } finally {
    cleanup();
    configureActivity();
  }
});
