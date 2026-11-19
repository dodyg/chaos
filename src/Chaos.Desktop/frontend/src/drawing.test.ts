import test from "node:test";
import assert from "node:assert/strict";
import {
  eraseAt,
  isUsableRegion,
  outlinePath,
  parseDrawing,
  regionBetween,
  serializeDrawing,
  StrokeHistory,
  strokeHit,
  strokeOutline,
  type Drawing,
  type Stroke,
} from "./drawing";

const line = (x0: number, y0: number, x1: number, y1: number, tool: Stroke["tool"] = "pen"): Stroke => ({
  tool,
  color: "#111827",
  size: 4,
  points: [
    [x0, y0, 0.5],
    [x1, y1, 0.5],
  ],
});

test("a drawing survives a serialize/parse round trip, rounded to two decimals", () => {
  const drawing: Drawing = {
    v: 1,
    strokes: [
      {
        tool: "highlighter",
        color: "#2563eb",
        size: 6,
        points: [
          [1.23456, 2.34567, 0.55555],
          [10, 20, 0.5],
        ],
      },
    ],
  };
  const back = parseDrawing(serializeDrawing(drawing));
  assert.equal(back.strokes.length, 1);
  assert.equal(back.strokes[0].tool, "highlighter");
  assert.deepEqual(back.strokes[0].points[0], [1.23, 2.35, 0.56]);
});

test("parsing is tolerant: bad JSON, wrong shapes and bad fields fall back to safe values", () => {
  assert.deepEqual(parseDrawing("not json").strokes, []);
  assert.deepEqual(parseDrawing(null).strokes, []);
  assert.deepEqual(parseDrawing('{"v":1}').strokes, []);
  const parsed = parseDrawing(
    JSON.stringify({
      v: 1,
      strokes: [
        {
          tool: "laser",
          color: "red",
          size: -3,
          points: [
            [1, 2],
            ["x", 2],
            [Number.NaN, 1],
            [3, 4, 9],
          ],
        },
        { points: [] },
        "junk",
      ],
    }),
  );
  assert.equal(parsed.strokes.length, 1);
  assert.equal(parsed.strokes[0].tool, "pen");
  assert.equal(parsed.strokes[0].color, "#111827");
  assert.equal(parsed.strokes[0].size, 6);
  assert.deepEqual(parsed.strokes[0].points, [
    [1, 2, 0.5],
    [3, 4, 0.5],
  ]);
});

test("the stroke eraser removes only strokes under the point and keeps the object when nothing is hit", () => {
  const drawing: Drawing = { v: 1, strokes: [line(0, 0, 100, 0), line(0, 50, 100, 50)] };
  assert.equal(eraseAt(drawing, 50, 200, 5), drawing);
  const erased = eraseAt(drawing, 50, 3, 5);
  assert.equal(erased.strokes.length, 1);
  assert.equal(erased.strokes[0].points[0][1], 50);
  assert.equal(drawing.strokes.length, 2, "the original is not changed");
});

test("hit testing counts the ink width and handles single-point strokes", () => {
  const dot: Stroke = { tool: "pen", color: "#111827", size: 10, points: [[20, 20, 0.5]] };
  assert.equal(strokeHit(dot, 24, 20, 2), true);
  assert.equal(strokeHit(dot, 40, 20, 2), false);
  const wide = line(0, 0, 100, 0, "highlighter");
  assert.equal(strokeHit(wide, 50, 6, 1), true, "a highlighter is three times as wide");
  assert.equal(strokeHit(line(0, 0, 100, 0), 50, 6, 1), false, "the same distance misses a pen stroke");
});

test("a region is the rectangle between two corners in either direction, kept inside the drawing", () => {
  assert.deepEqual(regionBetween({ x: 50, y: 40 }, { x: 10, y: 20 }, 100, 100), { x: 10, y: 20, w: 40, h: 20 });
  assert.deepEqual(regionBetween({ x: -20, y: 90 }, { x: 150, y: 120 }, 100, 100), { x: 0, y: 90, w: 100, h: 10 });
  assert.equal(isUsableRegion({ x: 0, y: 0, w: 3, h: 50 }), false);
  assert.equal(isUsableRegion(null), false);
  assert.equal(isUsableRegion({ x: 0, y: 0, w: 30, h: 50 }), true);
});

test("history undoes and redoes whole changes and forgets the redo branch on a new change", () => {
  const a: Drawing = { v: 1, strokes: [] };
  const b: Drawing = { v: 1, strokes: [line(0, 0, 1, 1)] };
  const c: Drawing = { v: 1, strokes: [line(0, 0, 1, 1), line(2, 2, 3, 3)] };
  const history = new StrokeHistory();
  history.push(a);
  history.push(b);
  assert.equal(history.undo(c), b);
  assert.equal(history.undo(b), a);
  assert.equal(history.undo(a), null);
  assert.equal(history.redo(a), b);
  assert.equal(history.canRedo, true);
  history.push(b);
  assert.equal(history.canRedo, false);
});

test("outlines and paths are produced for strokes of any length", () => {
  const single: Stroke = { tool: "pen", color: "#111827", size: 6, points: [[5, 5, 0.5]] };
  const long: Stroke = {
    tool: "pen",
    color: "#111827",
    size: 6,
    points: Array.from({ length: 30 }, (_, i) => [i * 3, Math.sin(i / 3) * 10 + 20, 0.5] as [number, number, number]),
  };
  assert.match(outlinePath(strokeOutline(single)), /^M /);
  const path = outlinePath(strokeOutline(long));
  assert.match(path, /^M .* Q .* Z$/);
  assert.equal(outlinePath([]), "");
});
