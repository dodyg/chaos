import { getStroke } from "perfect-freehand";

/**
 * The pad drawing model (PLAN2 P10): pure data and geometry, no DOM. A drawing is stored as
 * `{"v":1,"strokes":[...]}`, or since PLAN3 Q11 `{"v":2,"strokes":[...],"erased":[ids]}` in which each stroke has a stable `id` and a
 * `createdAt`, so two members drawing on one drawing can be merged by uniting strokes and removing erased ones; each stroke is a point list `[x, y, pressure]` in drawing units, the
 * units of the canvas the strokes were drawn on, so a drawing looks the same at any display size.
 */

export type Point = [x: number, y: number, pressure: number];
export type InkTool = "pen" | "highlighter";
export type Stroke = { tool: InkTool; color: string; size: number; points: Point[]; id?: string; createdAt?: number };
export type Drawing = { v: 1 | 2; strokes: Stroke[]; erased?: string[] };
export type Region = { x: number; y: number; w: number; h: number };

export const emptyDrawing: Drawing = { v: 1, strokes: [] };

/** The ink palette; every colour reads on the white paper the canvas always uses. */
export const palette: readonly { name: string; value: string }[] = [
  { name: "Black", value: "#111827" },
  { name: "Blue", value: "#2563eb" },
  { name: "Red", value: "#dc2626" },
  { name: "Green", value: "#16a34a" },
  { name: "Orange", value: "#ea580c" },
  { name: "Purple", value: "#9333ea" },
];

export const penSizes: readonly { name: string; value: number }[] = [
  { name: "Fine", value: 3 },
  { name: "Medium", value: 6 },
  { name: "Bold", value: 12 },
];

/** Highlighter ink is wider than the pen at the same setting and drawn translucent. */
export const highlighterScale = 3;
export const highlighterAlpha = 0.35;

const colorPattern = /^#[0-9a-fA-F]{6}$/;

export function parseDrawing(json: string | null | undefined): Drawing {
  if (!json) return { v: 1, strokes: [] };
  try {
    const raw = JSON.parse(json) as { v?: unknown; strokes?: unknown; erased?: unknown };
    if (!raw || !Array.isArray(raw.strokes)) return { v: 1, strokes: [] };
    const strokes: Stroke[] = [];
    for (const item of raw.strokes as Record<string, unknown>[]) {
      if (!item || !Array.isArray(item.points)) continue;
      const points: Point[] = [];
      for (const p of item.points as unknown[]) {
        if (!Array.isArray(p) || typeof p[0] !== "number" || typeof p[1] !== "number" || !Number.isFinite(p[0]) || !Number.isFinite(p[1]))
          continue;
        points.push([p[0], p[1], typeof p[2] === "number" && p[2] > 0 && p[2] <= 1 ? p[2] : 0.5]);
      }
      if (points.length === 0) continue;
      const color = typeof item.color === "string" && colorPattern.test(item.color) ? item.color : palette[0].value;
      const size = typeof item.size === "number" && item.size > 0 && item.size <= 200 ? item.size : penSizes[1].value;
      const stroke: Stroke = { tool: item.tool === "highlighter" ? "highlighter" : "pen", color, size, points };
      if (raw.v === 2 && typeof item.id === "string" && item.id) {
        stroke.id = item.id;
        stroke.createdAt = typeof item.createdAt === "number" && Number.isFinite(item.createdAt) ? item.createdAt : 0;
      }
      strokes.push(stroke);
    }
    if (raw.v === 2) {
      const erased = Array.isArray(raw.erased) ? (raw.erased as unknown[]).filter((id): id is string => typeof id === "string") : [];
      return { v: 2, strokes, erased };
    }
    return { v: 1, strokes };
  } catch {
    return { v: 1, strokes: [] };
  }
}

/** Coordinates keep two decimals: enough for any display, and a long drawing stays small. */
export function serializeDrawing(drawing: Drawing): string {
  const round = (n: number) => Math.round(n * 100) / 100;
  const strokes = drawing.strokes.map((s) => ({
    ...(drawing.v === 2 ? { id: s.id, createdAt: s.createdAt ?? 0 } : {}),
    tool: s.tool,
    color: s.color,
    size: s.size,
    points: s.points.map(([x, y, p]) => [round(x), round(y), round(p)]),
  }));
  return JSON.stringify(drawing.v === 2 ? { v: 2, strokes, erased: drawing.erased ?? [] } : { v: 1, strokes });
}

/**
 * A v1 drawing becomes v2 the first time it is edited: its strokes get the IDs `legacy-0`, `legacy-1`, … in order and
 * `createdAt` 0, the same IDs the host's merge gives a v1 drawing, so both sides agree on which stroke is which.
 */
export function upgradeDrawing(drawing: Drawing): Drawing & { v: 2; erased: string[] } {
  if (drawing.v === 2) return { ...drawing, v: 2, erased: drawing.erased ?? [] };
  return {
    v: 2,
    strokes: drawing.strokes.map((stroke, index) => ({ ...stroke, id: `legacy-${index}`, createdAt: 0 })),
    erased: [],
  };
}

/**
 * The v2 drawing `next` as an edit of `previous`: a stroke that was in `previous` and is gone from `next` is recorded as erased
 * (so a copy of it on another device does not bring it back), and one that is back in `next` (a redo) stops being erased.
 */
export function reconcileErased(previous: Drawing, next: Drawing): Drawing & { v: 2; erased: string[] } {
  const before = upgradeDrawing(previous);
  const after = upgradeDrawing(next);
  const present = new Set(after.strokes.map((stroke) => stroke.id));
  const erased = new Set(after.erased);
  for (const stroke of before.strokes) if (stroke.id && !present.has(stroke.id)) erased.add(stroke.id);
  for (const id of present) if (id) erased.delete(id);
  return { v: 2, strokes: after.strokes, erased: [...erased] };
}

/** The filled outline of a stroke (perfect-freehand), as polygon vertices. */
export function strokeOutline(stroke: Stroke): number[][] {
  const width = stroke.tool === "highlighter" ? stroke.size * highlighterScale : stroke.size;
  return getStroke(stroke.points, {
    size: width,
    thinning: stroke.tool === "highlighter" ? 0 : 0.5,
    smoothing: 0.5,
    streamline: 0.5,
    simulatePressure: false,
    last: true,
  });
}

/** An SVG path (also valid for Path2D) through the outline's vertices, smoothed with midpoints. */
export function outlinePath(outline: number[][]): string {
  if (outline.length === 0) return "";
  if (outline.length < 3) {
    const [x, y] = outline[0];
    return `M ${x} ${y} h 0.01`;
  }
  const mid = (a: number[], b: number[]) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
  let d = `M ${outline[0][0].toFixed(2)} ${outline[0][1].toFixed(2)} Q`;
  for (let i = 0; i < outline.length; i++) {
    const [x0, y0] = outline[i];
    const [x1, y1] = mid(outline[i], outline[(i + 1) % outline.length]);
    d += ` ${x0.toFixed(2)} ${y0.toFixed(2)} ${x1.toFixed(2)} ${y1.toFixed(2)}`;
  }
  return `${d} Z`;
}

function distanceToSegment(px: number, py: number, ax: number, ay: number, bx: number, by: number): number {
  const dx = bx - ax;
  const dy = by - ay;
  const lengthSquared = dx * dx + dy * dy;
  const t = lengthSquared === 0 ? 0 : Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / lengthSquared));
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
}

/** Whether a stroke passes within `radius` of the point, counting the ink's own width. */
export function strokeHit(stroke: Stroke, x: number, y: number, radius: number): boolean {
  const reach = radius + (stroke.tool === "highlighter" ? stroke.size * highlighterScale : stroke.size) / 2;
  const points = stroke.points;
  if (points.length === 1) return Math.hypot(points[0][0] - x, points[0][1] - y) <= reach;
  for (let i = 1; i < points.length; i++) {
    if (distanceToSegment(x, y, points[i - 1][0], points[i - 1][1], points[i][0], points[i][1]) <= reach) return true;
  }
  return false;
}

/** Stroke-level eraser: removes every stroke under the point. Returns the same object when none was hit. */
export function eraseAt(drawing: Drawing, x: number, y: number, radius: number): Drawing {
  const kept = drawing.strokes.filter((stroke) => !strokeHit(stroke, x, y, radius));
  if (kept.length === drawing.strokes.length) return drawing;
  return reconcileErased(drawing, { ...drawing, strokes: kept });
}

/** The rectangle between two corner points, whichever way it was dragged, kept inside the drawing. */
export function regionBetween(a: { x: number; y: number }, b: { x: number; y: number }, width: number, height: number): Region {
  const clamp = (value: number, max: number) => Math.max(0, Math.min(max, value));
  const x0 = clamp(Math.min(a.x, b.x), width);
  const y0 = clamp(Math.min(a.y, b.y), height);
  const x1 = clamp(Math.max(a.x, b.x), width);
  const y1 = clamp(Math.max(a.y, b.y), height);
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

/** A region too small to mean anything (a stray click) is not a selection. */
export const minRegionSide = 8;
export function isUsableRegion(region: Region | null): region is Region {
  return !!region && region.w >= minRegionSide && region.h >= minRegionSide;
}

/** Undo/redo within one editing session; snapshots are immutable drawings. */
export class StrokeHistory {
  private past: Drawing[] = [];
  private future: Drawing[] = [];

  /** Records the state before a change; a new change forgets what could be redone. */
  push(before: Drawing) {
    this.past.push(before);
    if (this.past.length > 200) this.past.shift();
    this.future = [];
  }

  get canUndo() {
    return this.past.length > 0;
  }
  get canRedo() {
    return this.future.length > 0;
  }

  undo(current: Drawing): Drawing | null {
    const previous = this.past.pop();
    if (!previous) return null;
    this.future.push(current);
    return previous;
  }

  redo(current: Drawing): Drawing | null {
    const next = this.future.pop();
    if (!next) return null;
    this.past.push(current);
    return next;
  }

  clear() {
    this.past = [];
    this.future = [];
  }
}

/**
 * Unites two versions of one drawing, as the host's merge does: strokes by ID, minus the erased IDs of both, ordered by
 * `(createdAt, id)`. Used when another member's strokes arrive while this window holds strokes of its own.
 */
export function mergeDrawings(a: Drawing, b: Drawing): Drawing & { v: 2; erased: string[] } {
  const left = upgradeDrawing(a);
  const right = upgradeDrawing(b);
  const erased = new Set([...left.erased, ...right.erased]);
  const byId = new Map<string, Stroke>();
  for (const stroke of [...left.strokes, ...right.strokes])
    if (stroke.id && !erased.has(stroke.id) && !byId.has(stroke.id)) byId.set(stroke.id, stroke);
  const strokes = [...byId.values()].sort(
    (x, y) => (x.createdAt ?? 0) - (y.createdAt ?? 0) || (x.id! < y.id! ? -1 : x.id! > y.id! ? 1 : 0),
  );
  return { v: 2, strokes, erased: [...erased].sort() };
}
