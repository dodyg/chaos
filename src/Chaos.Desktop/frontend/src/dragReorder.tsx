import React from "react";
import { GripVertical } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * Final position of an item dropped on a target row, using the same insertion-index
 * semantics as the keyboard Move up/down buttons (the index after the item is removed).
 */
export function dropInsertionIndex(sourceIndex: number, targetIndex: number, after: boolean) {
  const boundary = targetIndex + (after ? 1 : 0);
  return boundary - (sourceIndex < boundary ? 1 : 0);
}

type Dragged = { id: string; group: string };

/** Which axis a drop is measured along: rows (default) or board columns. */
export type DropAxis = "y" | "x";

type TargetOptions = {
  /** The axis the pointer position is compared against to decide "before" or "after". */
  axis?: DropAxis;
  /** Also accept items dragged in from another group (a board card dropped on another column). */
  foreign?: boolean;
};

/**
 * Native HTML drag/drop reordering scoped to one sibling set ("group"). Drops onto another
 * group are refused (unless `foreign` opts in, as a board column does) so an item is never
 * reordered against the wrong siblings. Every use must keep its keyboard alternative.
 */
export function useDragReorder() {
  const dragged = React.useRef<Dragged | null>(null);

  function handle(id: string, group: string, disabled: boolean, className?: string, keyboard?: { label: string; onMove: (offset: number) => void }) {
    if (keyboard) return <button type="button" disabled={disabled} draggable={!disabled} aria-label={keyboard.label}
      title="Drag to reorder, or use Up and Down arrow keys" aria-keyshortcuts="ArrowUp ArrowDown"
      className={cn("inline-flex shrink-0 cursor-grab items-center rounded-sm px-0.5 py-1 text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 active:cursor-grabbing disabled:cursor-default disabled:opacity-40", className)}
      onDragStart={event => { event.stopPropagation(); dragged.current = { id, group }; event.dataTransfer.effectAllowed = "move"; event.dataTransfer.setData("text/plain", id); }}
      onDragEnd={() => { dragged.current = null; }}
      onKeyDown={event => {
        if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
        event.preventDefault(); keyboard.onMove(event.key === "ArrowUp" ? -1 : 1);
      }}><GripVertical aria-hidden="true" className="size-4"/></button>;
    return <span className={cn("inline-flex shrink-0 cursor-grab items-center px-0.5 py-1 text-muted-foreground opacity-40 transition-opacity group-hover/row:opacity-90 data-[disabled=true]:cursor-default", className)}
      data-disabled={disabled} draggable={!disabled} aria-hidden="true" title="Drag to reorder"
      onDragStart={event => { dragged.current = { id, group }; event.dataTransfer.effectAllowed = "move"; event.dataTransfer.setData("text/plain", id); }}
      onDragEnd={() => { dragged.current = null; }}><GripVertical className="size-4"/></span>;
  }

  /** Props for a row; `order` is the sibling IDs in display order and `index` the row's position in it. */
  function target(order: readonly string[], index: number, group: string, disabled: boolean, reorder: (id: string, insertionIndex: number) => void, options?: TargetOptions) {
    const accepts = (source: Dragged | null): source is Dragged =>
      !disabled && source !== null && source.id !== order[index] && (source.group === group || options?.foreign === true);
    const after = (event: React.DragEvent<HTMLElement>, box: DOMRect) =>
      options?.axis === "x" ? event.clientX >= box.left + box.width / 2 : event.clientY >= box.top + box.height / 2;
    return {
      onDragOver: (event: React.DragEvent<HTMLElement>) => {
        if (!accepts(dragged.current)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
      },
      onDrop: (event: React.DragEvent<HTMLElement>) => {
        const source = dragged.current;
        dragged.current = null;
        if (!source) return;
        // The pointer was over this row: the group container must not treat it as an append.
        event.stopPropagation();
        if (!accepts(source)) return;
        const sourceIndex = order.indexOf(source.id);
        event.preventDefault();
        const box = event.currentTarget.getBoundingClientRect();
        const boundary = index + (after(event, box) ? 1 : 0);
        const insertionIndex = sourceIndex >= 0 && sourceIndex < boundary ? boundary - 1 : boundary;
        if (insertionIndex !== sourceIndex) reorder(source.id, insertionIndex);
      },
    };
  }

  /** Props for a whole group container: catches drops on its empty space (append). */
  function zone(disabled: boolean, drop: (id: string) => void) {
    return {
      onDragOver: (event: React.DragEvent<HTMLElement>) => {
        if (disabled || !dragged.current) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
      },
      onDrop: (event: React.DragEvent<HTMLElement>) => {
        const source = dragged.current;
        dragged.current = null;
        if (!source || disabled) return;
        event.preventDefault();
        drop(source.id);
      },
    };
  }

  return { handle, target, zone };
}
