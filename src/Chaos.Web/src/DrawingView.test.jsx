import { test } from "node:test";
import assert from "node:assert/strict";
import { parseStrokes, strokePath } from "./DrawingView.jsx";

const json = JSON.stringify({
  v: 1,
  strokes: [
    {
      tool: "pen",
      color: "#2563eb",
      size: 6,
      points: [
        [0, 0, 0.5],
        [10, 10, 0.5],
        [20, 0, 0.5],
      ],
    },
    { tool: "highlighter", color: "bad", size: -1, points: [[5, 5, 0.5]] },
    { tool: "pen", points: [] },
    { tool: "pen", points: [["x", 1]] },
  ],
});

test("strokes are read with safe defaults and empty or broken ones dropped", () => {
  const strokes = parseStrokes(json);
  assert.equal(strokes.length, 2);
  assert.equal(strokes[0].color, "#2563eb");
  assert.equal(strokes[1].highlighter, true);
  assert.equal(strokes[1].color, "#111827");
  assert.equal(strokes[1].size, 6);
});

test("bad JSON gives no strokes", () => {
  assert.deepEqual(parseStrokes("not json"), []);
  assert.deepEqual(parseStrokes('{"v":1}'), []);
});

test("a path runs through every point and a lone point becomes a dot", () => {
  assert.equal(
    strokePath([
      [0, 0],
      [10, 10],
      [20, 0],
    ]),
    "M 0 0 Q 10 10 15 5 L 20 0",
  );
  assert.equal(strokePath([[5, 5]]), "M 5 5 h 0.01");
});
