import React from "react";
import { Eraser, Highlighter, PenLine, Redo2, SquareDashedMousePointer, Undo2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  eraseAt,
  reconcileErased,
  upgradeDrawing,
  highlighterAlpha,
  isUsableRegion,
  outlinePath,
  palette,
  penSizes,
  regionBetween,
  StrokeHistory,
  strokeOutline,
  type Drawing,
  type Point,
  type Region,
  type Stroke,
} from "./drawing";

export type DrawingTool = "pen" | "highlighter" | "eraser" | "select";

type Props = {
  width: number;
  height: number;
  /** The drawing to show. Changing it from outside (a reload) replaces the canvas content and clears undo. */
  value: Drawing;
  /** Called with the new drawing after every completed change (a stroke, an erase, undo, redo). */
  onChange: (drawing: Drawing) => void;
  /** A usable rectangle chosen with the select tool, or null once cleared. */
  onSelectRegion?: (region: Region | null) => void;
  /** A region to outline, in drawing units (the backlink of a task). */
  highlight?: Region | null;
  readOnly?: boolean;
  label?: string;
};

/** Draws one stroke onto a 2D context in drawing units. */
export function paintStroke(ctx: CanvasRenderingContext2D, stroke: Stroke) {
  const path = outlinePath(strokeOutline(stroke));
  if (!path) return;
  ctx.save();
  ctx.fillStyle = stroke.color;
  ctx.globalAlpha = stroke.tool === "highlighter" ? highlighterAlpha : 1;
  ctx.fill(new Path2D(path));
  ctx.restore();
}

/** Paints a whole drawing on white paper. Highlighter strokes go first so ink stays readable over them. */
export function paintDrawing(ctx: CanvasRenderingContext2D, drawing: Drawing, width: number, height: number) {
  ctx.save();
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(0, 0, width, height);
  for (const stroke of drawing.strokes) if (stroke.tool === "highlighter") paintStroke(ctx, stroke);
  for (const stroke of drawing.strokes) if (stroke.tool === "pen") paintStroke(ctx, stroke);
  ctx.restore();
}

/** A PNG of the drawing at its own size, for the preview attachment. Null when canvas is unavailable. */
export async function renderDrawingPng(drawing: Drawing, width: number, height: number): Promise<Blob | null> {
  if (typeof document === "undefined") return null;
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d");
  if (!ctx || typeof Path2D === "undefined") return null;
  paintDrawing(ctx, drawing, width, height);
  return await new Promise<Blob | null>((resolve) => canvas.toBlob((blob) => resolve(blob), "image/png"));
}

const eraserRadius = 10;

/**
 * The drawing surface (PLAN2 P10): pointer events for mouse, pen (with pressure) and touch;
 * pen, highlighter, a stroke-level eraser and a rectangle select tool; undo and redo within
 * the editing session. The surface is always white paper so the palette reads in dark mode.
 * Drawing is a pointer activity: the keyboard can pick tools, undo and redo, and clear a
 * selection, but it cannot draw.
 */
export function DrawingCanvas({ width, height, value, onChange, onSelectRegion, highlight, readOnly, label = "Drawing" }: Props) {
  const canvasRef = React.useRef<HTMLCanvasElement | null>(null);
  const [tool, setTool] = React.useState<DrawingTool>("pen");
  const [color, setColor] = React.useState(palette[0].value);
  const [size, setSize] = React.useState(penSizes[1].value);
  const [drawing, setDrawing] = React.useState<Drawing>(value);
  const [region, setRegion] = React.useState<Region | null>(null);
  const [, bump] = React.useReducer((n: number) => n + 1, 0);
  const history = React.useRef(new StrokeHistory());
  const drawingRef = React.useRef(drawing);
  drawingRef.current = drawing;
  const live = React.useRef<{ stroke: Stroke | null; erasing: boolean; dragFrom: { x: number; y: number } | null; pointer: number } | null>(
    null,
  );
  const lastValue = React.useRef(value);

  React.useEffect(() => {
    if (value === lastValue.current) return;
    lastValue.current = value;
    history.current.clear();
    setDrawing(value);
    setRegion(null);
  }, [value]);

  const repaint = React.useCallback(() => {
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext?.("2d");
    if (!canvas || !ctx || typeof Path2D === "undefined") return;
    paintDrawing(ctx, drawingRef.current, width, height);
    const current = live.current?.stroke;
    if (current) paintStroke(ctx, current);
    const shown = region ?? highlight ?? null;
    if (shown) {
      ctx.save();
      ctx.strokeStyle = "#2563eb";
      ctx.fillStyle = "rgba(37, 99, 235, 0.12)";
      ctx.lineWidth = 3;
      ctx.setLineDash([12, 8]);
      ctx.fillRect(shown.x, shown.y, shown.w, shown.h);
      ctx.strokeRect(shown.x, shown.y, shown.w, shown.h);
      ctx.restore();
    }
  }, [width, height, region, highlight]);
  React.useEffect(repaint, [repaint, drawing]);

  /** Reports a change upward. The parent echoes it back as `value`, which must not look like an outside reload. */
  function emit(next: Drawing) {
    lastValue.current = next;
    onChange(next);
  }

  function commit(next: Drawing, before: Drawing) {
    history.current.push(before);
    setDrawing(next);
    drawingRef.current = next;
    emit(next);
    bump();
  }

  function toDrawingPoint(event: React.PointerEvent<HTMLCanvasElement>): Point {
    const rect = event.currentTarget.getBoundingClientRect();
    const scaleX = rect.width > 0 ? width / rect.width : 1;
    const scaleY = rect.height > 0 ? height / rect.height : 1;
    // A mouse reports pressure 0 while pressed (or 0.5 in some browsers); treat that as medium.
    const pressure = event.pointerType === "pen" && event.pressure > 0 ? event.pressure : 0.5;
    return [(event.clientX - rect.left) * scaleX, (event.clientY - rect.top) * scaleY, pressure];
  }

  function onPointerDown(event: React.PointerEvent<HTMLCanvasElement>) {
    if (readOnly || (event.pointerType === "mouse" && event.button !== 0)) return;
    event.preventDefault();
    try {
      event.currentTarget.setPointerCapture?.(event.pointerId);
    } catch {
      /* capture is best effort */
    }
    const [x, y, pressure] = toDrawingPoint(event);
    if (tool === "eraser") {
      live.current = { stroke: null, erasing: true, dragFrom: null, pointer: event.pointerId };
      eraseUnder(x, y);
    } else if (tool === "select") {
      live.current = { stroke: null, erasing: false, dragFrom: { x, y }, pointer: event.pointerId };
      setRegion(null);
      onSelectRegion?.(null);
    } else {
      live.current = {
        stroke: { tool, color, size, points: [[x, y, pressure]], id: crypto.randomUUID(), createdAt: Date.now() },
        erasing: false,
        dragFrom: null,
        pointer: event.pointerId,
      };
      repaint();
    }
  }

  /** One erase gesture is one undo step: the state before the first removal is what undo restores. */
  const eraseBefore = React.useRef<Drawing | null>(null);
  function eraseUnder(x: number, y: number) {
    const current = drawingRef.current;
    const next = eraseAt(current, x, y, eraserRadius);
    if (next === current) return;
    eraseBefore.current ??= current;
    setDrawing(next);
    drawingRef.current = next;
  }

  function onPointerMove(event: React.PointerEvent<HTMLCanvasElement>) {
    const gesture = live.current;
    if (!gesture || gesture.pointer !== event.pointerId) return;
    // Coalesced events keep fast pen strokes smooth; fall back to the event itself.
    const events = event.nativeEvent.getCoalescedEvents?.() ?? [];
    const rect = event.currentTarget.getBoundingClientRect();
    const scaleX = rect.width > 0 ? width / rect.width : 1;
    const scaleY = rect.height > 0 ? height / rect.height : 1;
    const samples = events.length > 0 ? events : [event.nativeEvent];
    for (const sample of samples) {
      const x = (sample.clientX - rect.left) * scaleX;
      const y = (sample.clientY - rect.top) * scaleY;
      if (gesture.erasing) eraseUnder(x, y);
      else if (gesture.stroke)
        gesture.stroke.points.push([x, y, event.pointerType === "pen" && sample.pressure > 0 ? sample.pressure : 0.5]);
      else if (gesture.dragFrom) setRegion(regionBetween(gesture.dragFrom, { x, y }, width, height));
    }
    if (gesture.stroke) repaint();
  }

  function onPointerUp(event: React.PointerEvent<HTMLCanvasElement>) {
    const gesture = live.current;
    if (!gesture || gesture.pointer !== event.pointerId) return;
    live.current = null;
    if (gesture.stroke) {
      const before = drawingRef.current;
      const upgraded = upgradeDrawing(before);
      commit({ v: 2, strokes: [...upgraded.strokes, gesture.stroke], erased: upgraded.erased }, before);
    } else if (gesture.erasing && eraseBefore.current) {
      const before = eraseBefore.current;
      eraseBefore.current = null;
      history.current.push(before);
      emit(drawingRef.current);
      bump();
    } else if (gesture.dragFrom) {
      const [endX, endY] = toDrawingPoint(event);
      const chosen = regionBetween(gesture.dragFrom, { x: endX, y: endY }, width, height);
      if (isUsableRegion(chosen)) {
        setRegion(chosen);
        onSelectRegion?.(chosen);
      } else {
        setRegion(null);
        onSelectRegion?.(null);
      }
    }
  }

  function undo() {
    const restored = history.current.undo(drawingRef.current);
    if (!restored) return;
    const previous = reconcileErased(drawingRef.current, restored);
    setDrawing(previous);
    drawingRef.current = previous;
    emit(previous);
    bump();
  }

  function redo() {
    const redone = history.current.redo(drawingRef.current);
    if (!redone) return;
    const next = reconcileErased(drawingRef.current, redone);
    setDrawing(next);
    drawingRef.current = next;
    emit(next);
    bump();
  }

  function clearRegion() {
    setRegion(null);
    onSelectRegion?.(null);
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    const mod = event.ctrlKey || event.metaKey;
    if (mod && event.key.toLowerCase() === "z" && !event.shiftKey) {
      event.preventDefault();
      undo();
    } else if (mod && (event.key.toLowerCase() === "y" || (event.key.toLowerCase() === "z" && event.shiftKey))) {
      event.preventDefault();
      redo();
    } else if (event.key === "Escape" && region) {
      event.preventDefault();
      clearRegion();
    }
  }

  const tools: { id: DrawingTool; name: string; icon: React.ReactNode }[] = [
    { id: "pen", name: "Pen", icon: <PenLine /> },
    { id: "highlighter", name: "Highlighter", icon: <Highlighter /> },
    { id: "eraser", name: "Eraser", icon: <Eraser /> },
    { id: "select", name: "Select a part", icon: <SquareDashedMousePointer /> },
  ];
  const inkTool = tool === "pen" || tool === "highlighter";

  return (
    <div className="grid gap-2" onKeyDown={onKeyDown}>
      {!readOnly && (
        <div role="toolbar" aria-label="Drawing tools" className="flex flex-wrap items-center gap-1.5">
          {tools.map((item) => (
            <Button
              key={item.id}
              type="button"
              size="sm"
              variant={tool === item.id ? "secondary" : "outline"}
              aria-pressed={tool === item.id}
              onClick={() => {
                setTool(item.id);
                if (item.id !== "select") clearRegion();
              }}
            >
              {item.icon}
              {item.name}
            </Button>
          ))}
          <span className="mx-1 h-5 w-px bg-border" aria-hidden="true" />
          <div role="group" aria-label="Ink colour" className="flex gap-1">
            {palette.map((swatch) => (
              <button
                key={swatch.value}
                type="button"
                aria-label={swatch.name}
                aria-pressed={color === swatch.value}
                disabled={!inkTool}
                className={cn(
                  "size-6 rounded-full border-2 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-40",
                  color === swatch.value ? "border-primary ring-1 ring-primary" : "border-border",
                )}
                style={{ backgroundColor: swatch.value }}
                onClick={() => setColor(swatch.value)}
              />
            ))}
          </div>
          <div role="group" aria-label="Ink size" className="flex gap-1">
            {penSizes.map((option) => (
              <Button
                key={option.value}
                type="button"
                size="xs"
                variant={size === option.value ? "secondary" : "ghost"}
                aria-pressed={size === option.value}
                disabled={!inkTool}
                onClick={() => setSize(option.value)}
              >
                {option.name}
              </Button>
            ))}
          </div>
          <span className="mx-1 h-5 w-px bg-border" aria-hidden="true" />
          <Button
            type="button"
            size="icon-sm"
            variant="ghost"
            aria-label="Undo"
            title="Undo (Ctrl+Z)"
            disabled={!history.current.canUndo}
            onClick={undo}
          >
            <Undo2 />
          </Button>
          <Button
            type="button"
            size="icon-sm"
            variant="ghost"
            aria-label="Redo"
            title="Redo (Ctrl+Y)"
            disabled={!history.current.canRedo}
            onClick={redo}
          >
            <Redo2 />
          </Button>
        </div>
      )}
      <div className="overflow-auto rounded-lg border bg-white">
        <canvas
          ref={canvasRef}
          width={width}
          height={height}
          role="img"
          aria-label={`${label}: ${drawing.strokes.length} stroke${drawing.strokes.length === 1 ? "" : "s"}. Drawing needs a mouse, pen or touch.`}
          className={cn(
            "block h-auto w-full max-w-full touch-none select-none",
            !readOnly && (tool === "eraser" ? "cursor-cell" : "cursor-crosshair"),
          )}
          style={{ aspectRatio: `${width} / ${height}`, touchAction: "none" }}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
        />
      </div>
      {tool === "select" && !readOnly && (
        <p className="text-xs text-muted-foreground" role="status">
          {region
            ? "Part selected. Choose Make task, or press Escape to clear it."
            : "Drag over the part of the drawing you want a task for."}
        </p>
      )}
    </div>
  );
}
