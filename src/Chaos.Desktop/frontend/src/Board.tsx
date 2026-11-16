import React from "react";
import { boardRpc, type BoardCardResponse, type BoardColumnResponse, type BoardLaneResponse, type BoardSettingsDto, type TagResponse, type TaskResponse } from "#neoastra";
import { CalendarDays, CheckSquare, Clock, GripVertical, MoreHorizontal, Settings2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Count, Field, InlineError, SegmentedRadio } from "@/components/common";
import { DateText } from "./dates";
import { GitHubTaskMark } from "./GitHubTaskMark";
import { TagDot } from "./tags";
import { CategoryEdge, type Focus } from "./category";

const operationId = () => crypto.randomUUID();

type BoardApi = typeof boardRpc;
export type BoardLayout = "list" | "board";

/** The per-container layout choice is device-local and never changes tasks (PLAN2 §11.1). */
export const layoutKey = (containerId: string) => `chaos.boardLayout.${containerId}`;

export function loadLayout(containerId: string): BoardLayout {
  try {
    return window.localStorage.getItem(layoutKey(containerId)) === "board" ? "board" : "list";
  } catch {
    return "list";
  }
}

export function saveLayout(containerId: string, layout: BoardLayout) {
  try {
    window.localStorage.setItem(layoutKey(containerId), layout);
  } catch {
    // Private-browsing style storage failures just lose the preference.
  }
}

function parseJson<T>(json: string | null | undefined, fallback: T): T {
  try {
    return JSON.parse(json ?? "") as T;
  } catch {
    return fallback;
  }
}

const groupByChoices = [["Status", "Status"], ["Section", "Sections"], ["Priority", "Priority"], ["TagGroup", "Tag group"]] as const;
const swimlaneChoices = [["None", "No swimlanes"], ["Priority", "Priority"], ["TagGroup", "Tag group"], ["Category", "Work / Personal"]] as const;
/** A custom view spans lists: no sections to group by, and a lane can be a list. */
const viewGroupByChoices = groupByChoices.filter(([value]) => value !== "Section");
const viewSwimlaneChoices = [...swimlaneChoices, ["Container", "List or project"]] as const;

/** A settings draft being edited in the popover; saved as one board.updateSettings call. */
type SettingsDraft = {
  groupBy: string;
  swimlaneBy: string;
  columnTagId: string;
  swimlaneTagId: string;
  wip: Record<string, string>;
  hidden: string[];
};

function draftFrom(settings: BoardSettingsDto, columnKeys: readonly string[]): SettingsDraft {
  const wip = parseJson<Record<string, number>>(settings.wipLimitsJson, {});
  return {
    groupBy: settings.groupBy,
    swimlaneBy: settings.swimlaneBy,
    columnTagId: settings.columnTagId ?? "",
    swimlaneTagId: settings.swimlaneTagId ?? "",
    wip: Object.fromEntries(columnKeys.map(key => [key, wip[key] !== undefined ? String(wip[key]) : ""])),
    hidden: parseJson<string[]>(settings.hiddenColumnsJson, []),
  };
}

/**
 * Where a moved card goes, as board.move takes it: just before or just after the neighbouring
 * card the person saw, or neither to keep its place in the container's order.
 */
export type Placement = { beforeTaskId: string | null; afterTaskId: string | null };

const keepPlace: Placement = { beforeTaskId: null, afterTaskId: null };

/**
 * The placement of a card dropped into one board cell (a column within a swimlane). `cell` is
 * the cell's card IDs top to bottom, `slot` the drop position among them once the dragged
 * card is left out (0 = top, their count = bottom). Null when the card is already there.
 */
export function dropPlacement(cell: readonly string[], draggedId: string, slot: number): Placement | null {
  if (cell.indexOf(draggedId) === slot) return null;
  const others = cell.filter(id => id !== draggedId);
  if (slot < others.length) return { beforeTaskId: others[slot], afterTaskId: null };
  return others.length > 0 ? { beforeTaskId: null, afterTaskId: others[others.length - 1] } : keepPlace;
}

/** The custom drag type: only this board accepts it, so a dropped card never types into a field. */
const cardDragType = "application/x-chaos-board-card";

type Dragged = { cardId: string; columnKey: string; laneKey: string };

const cellKey = (laneKey: string, columnKey: string) => `${laneKey}|${columnKey}`;

/**
 * A Kanban board for one list or project (PLAN2 §11.1): columns from the board settings,
 * optional swimlanes, cards dragged or keyboard-moved between and within columns, WIP limit
 * warnings and a settings popover. Every move is one board.move call with the task's version.
 *
 * Dragging is native HTML drag and drop: the whole card is the drag source and each cell (a
 * column within a swimlane) is a drop zone that shows where the card will land. A card keeps
 * its swimlane (a lane is a task field board.move does not change), so other lanes refuse it.
 */
export function Board({ containerId = "", viewId, tags, focus, busy, revision, onOpen, onChanged, api = boardRpc }: {
  /** The list or project shown as a board; or <c>viewId</c> for the board layout of a custom view (PLAN2 P3), whose drops change only the column. */
  containerId?: string; viewId?: string; tags: TagResponse[]; focus: Focus; busy: boolean; revision: number;
  onOpen: (task: TaskResponse, viewing: boolean) => void; onChanged: () => void; api?: BoardApi;
}) {
  const [board, setBoard] = React.useState<{ columns: readonly BoardColumnResponse[]; lanes: readonly BoardLaneResponse[]; cards: readonly BoardCardResponse[]; settings: BoardSettingsDto; totalCount: number; truncated: boolean } | null>(null);
  const [loadError, setLoadError] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [reloads, setReloads] = React.useState(0);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [showCancelled, setShowCancelled] = React.useState(false);
  const [showSettings, setShowSettings] = React.useState(false);
  const [moving, setMoving] = React.useState(false);
  const [choice, setChoice] = React.useState<{ card: BoardCardResponse; toColumn: string; placement: Placement } | null>(null);
  const [menu, setMenu] = React.useState<{ cardId: string; page: "main" | "columns" } | null>(null);
  const [focusCardId, setFocusCardId] = React.useState<string | null>(null);
  // The drag in progress: the ref is read by the drop handlers, the state only styles the card.
  const dragged = React.useRef<Dragged | null>(null);
  const [draggingId, setDraggingId] = React.useState<string | null>(null);
  const [dropAt, setDropAt] = React.useState<{ cell: string; slot: number } | null>(null);
  const cardRefs = React.useRef(new Map<string, HTMLElement>());
  // The card to focus once the board has been read again after a move (its element may be new).
  const pendingFocus = React.useRef<string | null>(null);

  // `loading` keeps the board inert until a read lands, so no move goes out with the versions
  // of the board as it was before the last move.
  const disabled = busy || moving || loading;

  React.useEffect(() => {
    let current = true;
    setLoading(true);
    void (viewId ? api.getView({ viewId, category: focus, showCancelled }) : api.get({ containerId, category: focus, showCancelled })).then(result => {
      if (!current) return;
      if (result.ok) { setBoard(result); setLoadError(null); }
      else setLoadError(result.error?.message ?? "Could not load the board.");
      setLoading(false);
    }).catch((e: unknown) => { if (current) { setLoadError(String(e)); setLoading(false); } });
    return () => { current = false; };
  }, [api, containerId, viewId, focus, showCancelled, revision, reloads]);

  React.useEffect(() => {
    const id = pendingFocus.current;
    if (id === null || loading) return;
    pendingFocus.current = null;
    focusCard(id);
  }, [board, loading]);

  const settings = board?.settings;
  const hiddenKeys = new Set(parseJson<string[]>(settings?.hiddenColumnsJson, []));
  const wipLimits = parseJson<Record<string, number>>(settings?.wipLimitsJson, {});
  const columns = board ? board.columns.filter(column => !hiddenKeys.has(column.key)) : [];
  const lanes = board?.lanes ?? [{ key: "", title: "All tasks", isNone: true }];
  const multipleLanes = lanes.length > 1;
  const cards = board?.cards ?? [];
  const tagById = React.useMemo(() => new Map(tags.map(tag => [tag.id, tag])), [tags]);
  // The cards of one cell top to bottom (board order is the container's manual order).
  const cellCards = React.useMemo(() => {
    const cells = new Map<string, BoardCardResponse[]>();
    for (const card of board?.cards ?? []) {
      const key = cellKey(card.laneKey, card.columnKey);
      const cell = cells.get(key);
      if (cell) cell.push(card); else cells.set(key, [card]);
    }
    return cells;
  }, [board]);
  const cellOf = (card: BoardCardResponse) => cellCards.get(cellKey(card.laneKey, card.columnKey)) ?? [];
  // A column's cards as drawn: lane by lane, top to bottom.
  const columnOrder = (columnKey: string) => lanes.flatMap(lane => cellCards.get(cellKey(lane.key, columnKey)) ?? []);
  const visibleColumnKeys = new Set(columns.map(column => column.key));
  // The one card in the Tab order: the last focused card while it is still on the board.
  const tabStop = cards.some(card => card.task.id === focusCardId && visibleColumnKeys.has(card.columnKey))
    ? focusCardId
    : cards.find(card => visibleColumnKeys.has(card.columnKey))?.task.id ?? null;

  /** One board write: inert while it runs, its error shown, the board read again after it. */
  async function mutate(write: () => Promise<boolean>) {
    if (disabled) return;
    setMoving(true);
    setError(null);
    try {
      if (await write()) {
        // The write bumped versions (the moved card, cards reranked past it, the settings row):
        // read the board again so the next write does not send a stale version.
        setReloads(count => count + 1);
        onChanged();
      }
    } catch (e) {
      setError(String(e));
    } finally {
      setMoving(false);
    }
  }

  function moveCard(card: BoardCardResponse, toColumn: string, requested: Placement, subtaskChoice: string | null = null) {
    // A view orders its cards by its own sort, so a drop there only changes the column.
    const placement = viewId ? keepPlace : requested;
    return mutate(async () => {
      const result = await api.move({
        operationId: operationId(), taskId: card.task.id, expectedVersion: card.task.version,
        groupBy: settings?.groupBy ?? "Status", fromColumn: card.columnKey, toColumn, ...placement, subtaskChoice,
      });
      if (!result.ok) {
        // Moving to Done with open subtasks is a question, not a failure: ask and retry.
        if (result.error?.fields.some(field => field.fieldPath === "openSubtaskChoice")) {
          setChoice({ card, toColumn, placement });
          return false;
        }
        // The board was stale (a version conflict, a card gone): show why and read it again.
        setError(result.error?.message ?? "Could not move the task.");
        setReloads(count => count + 1);
        return false;
      }
      setNotice(result.warning ?? null);
      pendingFocus.current = card.task.id;
      return true;
    });
  }

  function saveSettings(next: SettingsDraft) {
    if (!board) return;
    const expectedVersion = board.settings.version;
    return mutate(async () => {
      const limits = Object.fromEntries(Object.entries(next.wip).filter(([, value]) => value !== "").map(([key, value]) => [key, Number(value)]));
      const result = await api.updateSettings({
        operationId: operationId(), targetKind: viewId ? "View" : "Container", targetId: viewId ?? containerId, expectedVersion,
        groupBy: next.groupBy, swimlaneBy: next.swimlaneBy,
        columnTagId: next.columnTagId || null, swimlaneTagId: next.swimlaneTagId || null,
        wipLimitsJson: JSON.stringify(limits), hiddenColumnsJson: JSON.stringify(next.hidden),
      });
      if (!result.ok || !result.settings) {
        setError(result.error?.message ?? "Could not save the board settings.");
        return false;
      }
      setShowSettings(false);
      // Cancelled cards only have a column (and a checkbox to hide them) when grouping by status.
      if (next.groupBy !== "Status") setShowCancelled(false);
      return true;
    });
  }

  function focusCard(id: string | undefined) {
    if (!id) return;
    setFocusCardId(id);
    cardRefs.current.get(id)?.focus();
  }

  function cardKeyDown(event: React.KeyboardEvent<HTMLElement>, card: BoardCardResponse) {
    if (event.target !== event.currentTarget) return; // the title button and menu handle their own keys
    switch (event.key) {
      case "ArrowDown":
      case "ArrowUp": {
        // Down the column as drawn, crossing into the next swimlane at a cell's end.
        event.preventDefault();
        const column = columnOrder(card.columnKey);
        focusCard(column[column.findIndex(item => item.task.id === card.task.id) + (event.key === "ArrowDown" ? 1 : -1)]?.task.id);
        break;
      }
      case "ArrowRight":
      case "ArrowLeft": {
        // The next column's card at the same height in the same swimlane, else its first card.
        event.preventDefault();
        const nextColumn = columns[columns.findIndex(column => column.key === card.columnKey) + (event.key === "ArrowRight" ? 1 : -1)];
        if (!nextColumn) return;
        const index = cellOf(card).findIndex(item => item.task.id === card.task.id);
        const sameLane = cellCards.get(cellKey(card.laneKey, nextColumn.key)) ?? [];
        focusCard((sameLane[Math.min(index, sameLane.length - 1)] ?? columnOrder(nextColumn.key)[0])?.task.id);
        break;
      }
      case "Enter":
      case " ":
        event.preventDefault();
        onOpen(card.task, false);
        break;
    }
  }

  function cardFace(card: BoardCardResponse) {
    const task = card.task;
    return <>
      <div className="flex items-start gap-0.5">
        <button type="button" dir="auto" className="min-w-0 flex-1 rounded-sm px-0.5 py-0.5 text-left text-sm font-medium [overflow-wrap:anywhere] outline-none hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50" disabled={disabled} onClick={() => onOpen(task, false)}>{task.title}</button>
        {card.hasIssueLink && <GitHubTaskMark taskId={task.id} version={task.version} revision={revision} linked/>}
      </div>
      <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1 px-0.5 text-xs text-muted-foreground">
        {task.dueDate
          ? <DateText kind="due" date={task.dueDate} time={task.dueTime}><CalendarDays className="size-3.5"/></DateText>
          : task.plannedDate
            ? <DateText kind="planned" date={task.plannedDate} time={task.plannedTime}><CalendarDays className="size-3.5"/></DateText>
            : null}
        {task.estimateMinutes !== null && <span className="inline-flex items-center gap-1"><Clock className="size-3.5"/>{task.estimateMinutes} min</span>}
        {card.subtasksTotal > 0 && <span className="inline-flex items-center gap-1"><CheckSquare className="size-3.5"/>{card.subtasksDone}/{card.subtasksTotal}</span>}
        {card.tagIds.length > 0 && <span className="inline-flex items-center gap-1" aria-label="Tags">{card.tagIds.map(id => tagById.get(id)).filter((tag): tag is TagResponse => !!tag).map(tag => <TagDot key={tag.id} color={tag.color}/>)}</span>}
      </div>
    </>;
  }

  function cardMenu(card: BoardCardResponse) {
    if (menu?.cardId !== card.task.id) return null;
    // Up and down move within the card's cell, as dragging does.
    const cell = cellOf(card);
    const index = cell.findIndex(item => item.task.id === card.task.id);
    const item = "flex w-full cursor-default rounded-sm px-2 py-1.5 text-left outline-none hover:bg-accent focus-visible:bg-accent focus-visible:outline-none disabled:opacity-50";
    return <div role="menu" aria-label={`Actions for ${card.task.title}`} className="absolute top-full right-0 z-30 mt-1 grid min-w-48 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-lg"
      onKeyDown={event => {
        if (event.key !== "Escape") return;
        setMenu(null);
        (event.currentTarget.previousElementSibling as HTMLElement | null)?.focus(); // back to the actions button
      }}>
      {menu.page === "main" ? <>
        <button type="button" role="menuitem" className={item} disabled={columns.length < 2} onClick={() => setMenu({ cardId: card.task.id, page: "columns" })}>Move to column…</button>
        {!viewId && <>
        <button type="button" role="menuitem" className={item} disabled={disabled || index <= 0} onClick={() => { setMenu(null); void moveCard(card, card.columnKey, { beforeTaskId: cell[index - 1].task.id, afterTaskId: null }); }}>Move up</button>
        <button type="button" role="menuitem" className={item} disabled={disabled || index >= cell.length - 1} onClick={() => { setMenu(null); void moveCard(card, card.columnKey, { beforeTaskId: null, afterTaskId: cell[index + 1].task.id }); }}>Move down</button></>}
      </> : <>
        {columns.filter(column => column.key !== card.columnKey).map(column => <button key={column.key} type="button" role="menuitem" className={item} disabled={disabled} onClick={() => { setMenu(null); void moveCard(card, column.key, keepPlace); }}>Move to {column.title}</button>)}
        <button type="button" role="menuitem" className={item} onClick={() => setMenu({ cardId: card.task.id, page: "main" })}>Back</button>
      </>}
    </div>;
  }

  function startDrag(event: React.DragEvent<HTMLElement>, card: BoardCardResponse) {
    // A link or image inside the card drags itself, not the card.
    if (event.target !== event.currentTarget || disabled) return;
    const started: Dragged = { cardId: card.task.id, columnKey: card.columnKey, laneKey: card.laneKey };
    dragged.current = started;
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData(cardDragType, card.task.id);
    // Chromium (WebView2) cancels a drag whose source changes during dragstart: restyle after it.
    window.setTimeout(() => {
      if (dragged.current !== started) return;
      setDraggingId(started.cardId);
      setMenu(null);
    }, 0);
  }

  function endDrag() {
    dragged.current = null;
    setDraggingId(null);
    setDropAt(null);
  }

  /** Drop-zone props for one cell: it shows where the card will land and moves it there on drop. */
  function cellDrop(column: BoardColumnResponse, lane: BoardLaneResponse, cell: readonly BoardCardResponse[]) {
    const key = cellKey(lane.key, column.key);
    const ids = cell.map(card => card.task.id);
    const accepted = () => !disabled && dragged.current !== null && dragged.current.laneKey === lane.key ? dragged.current : null;
    // How many of the cell's other cards sit above the pointer: midpoints, top to bottom.
    const slotAt = (source: Dragged, clientY: number) => ids.filter(id => {
      if (id === source.cardId) return false;
      const box = cardRefs.current.get(id)?.getBoundingClientRect();
      return box !== undefined && clientY > box.top + box.height / 2;
    }).length;
    return {
      onDragOver: (event: React.DragEvent<HTMLElement>) => {
        const source = accepted();
        if (!source) return; // not preventing the default refuses the drop
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        const slot = slotAt(source, event.clientY);
        setDropAt(current => current?.cell === key && current.slot === slot ? current : { cell: key, slot });
      },
      onDragLeave: (event: React.DragEvent<HTMLElement>) => {
        if (event.currentTarget.contains(event.relatedTarget as Node | null)) return; // into a child
        setDropAt(current => current?.cell === key ? null : current);
      },
      onDrop: (event: React.DragEvent<HTMLElement>) => {
        const source = accepted();
        if (!source) return;
        event.preventDefault();
        const placement = dropPlacement(ids, source.cardId, slotAt(source, event.clientY));
        endDrag();
        const card = cards.find(item => item.task.id === source.cardId);
        if (card && placement) void moveCard(card, column.key, placement);
      },
    };
  }

  function renderCard(card: BoardCardResponse, mark: "before" | "after" | null) {
    const task = card.task;
    return <li key={task.id}
      ref={element => { if (element) cardRefs.current.set(task.id, element); else cardRefs.current.delete(task.id); }}
      tabIndex={tabStop === task.id ? 0 : -1}
      aria-label={task.title}
      draggable={!disabled}
      className={cn("group/card relative grid gap-1 rounded-lg border bg-card p-2 shadow-xs outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50",
        !disabled && "cursor-grab active:cursor-grabbing", moving && "opacity-60", draggingId === task.id && "opacity-40")}
      onFocus={event => { if (event.target === event.currentTarget) setFocusCardId(task.id); }}
      onKeyDown={event => cardKeyDown(event, card)}
      onDragStart={event => startDrag(event, card)}
      onDragEnd={endDrag}>
      {mark && <span aria-hidden="true" className={cn("pointer-events-none absolute inset-x-1 h-0.5 rounded-full bg-primary", mark === "before" ? "-top-1" : "-bottom-1")}/>}
      <div className="flex items-start">
        <CategoryEdge category={task.category}/>
        <GripVertical aria-hidden="true" className={cn("mt-1 size-4 shrink-0 text-muted-foreground opacity-40 transition-opacity group-hover/card:opacity-90", disabled && "invisible")}/>
        <div className="grid min-w-0 flex-1 gap-1">{cardFace(card)}</div>
        <div className="relative">
          <Button variant="ghost" size="icon-sm" className="text-muted-foreground opacity-0 transition-opacity group-focus-within/card:opacity-100 group-hover/card:opacity-100 [@media(hover:none)]:opacity-100"
            disabled={disabled} aria-label={`Actions for ${task.title}`} aria-expanded={menu?.cardId === task.id} aria-haspopup="menu" onClick={() => setMenu(current => current?.cardId === task.id ? null : { cardId: task.id, page: "main" })}>
            <MoreHorizontal/>
          </Button>
          {cardMenu(card)}
        </div>
      </div>
    </li>;
  }

  function renderColumn(column: BoardColumnResponse, lane: BoardLaneResponse) {
    const count = cards.filter(card => card.columnKey === column.key).length;
    const key = cellKey(lane.key, column.key);
    const inLane = cellCards.get(key) ?? [];
    const limit = wipLimits[column.key];
    const over = limit !== undefined && count > limit;
    // The drop marker: a line before the card the dragged one would precede, or after the
    // last card. None while the drop would leave the card where it is.
    const target = dropAt?.cell === key && draggingId !== null && dropPlacement(inLane.map(card => card.task.id), draggingId, dropAt.slot);
    const markOf = (card: BoardCardResponse) => !target ? null
      : target.beforeTaskId === card.task.id ? "before" as const
        : target.afterTaskId === card.task.id ? "after" as const : null;
    return <section key={key} aria-label={`${lane.title} · ${column.title} column`}
      className={cn("flex w-64 shrink-0 grid-cols-1 flex-col gap-1.5 rounded-lg bg-muted/40 p-2 outline-none transition-colors",
        menu === null && "focus-within:ring-1 focus-within:ring-primary/40", target && "bg-primary/10 ring-1 ring-primary/40")}
      {...cellDrop(column, lane, inLane)}>
      <h4 className="flex items-center gap-1.5 px-1 text-xs font-semibold tracking-wider text-muted-foreground uppercase select-none">
        <span className="min-w-0 truncate">{column.title}</span>
        <span className="tabular-nums">{limit !== undefined
          ? <span className={over ? "font-medium text-amber-600 dark:text-amber-400" : undefined}>{count} / {limit}</span>
          : count}</span>
        {over && <span className="sr-only">over its WIP limit</span>}
      </h4>
      <ul className="grid content-start gap-1.5" aria-label={`${column.title} cards`}>
        {inLane.map(card => renderCard(card, markOf(card)))}
      </ul>
      {inLane.length === 0 && <p className="px-1 py-2 text-xs text-muted-foreground italic">{target ? "Drop here." : "No cards."}</p>}
    </section>;
  }

  return <div className="grid gap-3" aria-label="Board">
    <div className="flex flex-wrap items-center gap-3">
      <Button type="button" variant={showSettings ? "secondary" : "outline"} disabled={disabled} aria-expanded={showSettings} aria-controls="board-settings" onClick={() => setShowSettings(v => !v)}><Settings2/>{showSettings ? "Hide settings" : "Board settings"}</Button>
      {settings?.groupBy === "Status" && <label className="flex items-center gap-1.5 text-xs">
        <input type="checkbox" className="size-4 accent-primary" checked={showCancelled} disabled={disabled} onChange={e => setShowCancelled(e.target.checked)}/>
        Show cancelled
      </label>}
      {board && board.truncated && <p role="status" className="text-xs text-muted-foreground">{board.cards.length} of {board.totalCount} shown, filter to see more</p>}
    </div>
    {/* Keyed by the settings version: a newer row (saved here or elsewhere) starts a fresh draft. */}
    {showSettings && board && <BoardSettingsPanel key={board.settings.version} forView={!!viewId} settings={board.settings} columns={board.columns} tags={tags} disabled={disabled}
      onSave={next => void saveSettings(next)} onCancel={() => setShowSettings(false)}/>}
    {(loadError ?? error) && <InlineError>{loadError ?? error}</InlineError>}
    {notice && <div role="status" className="rounded-lg border bg-muted/50 px-3 py-2 text-sm text-muted-foreground">{notice}</div>}
    {board && !loadError && <div className="-mx-1 overflow-x-auto px-1 pb-1">
      <div className="grid w-max min-w-full gap-4">
        {lanes.map(lane => <section key={lane.key} aria-label={multipleLanes ? `${lane.title} swimlane` : "Board lanes"} className="grid gap-1.5">
          {multipleLanes && <h3 className="flex items-center px-1 text-xs font-semibold tracking-wider text-muted-foreground uppercase">{lane.title}<Count>{cards.filter(card => card.laneKey === lane.key && visibleColumnKeys.has(card.columnKey)).length}</Count></h3>}
          <div className="flex items-start gap-3">
            {columns.map(column => renderColumn(column, lane))}
            {columns.length === 0 && <p className="p-2 text-sm text-muted-foreground">Every column is hidden. Unhide them in the board settings.</p>}
          </div>
        </section>)}
      </div>
    </div>}
    {choice && <div className="fixed inset-0 z-40 grid place-items-center bg-black/40 p-4">
      <div role="dialog" aria-modal="true" aria-labelledby="board-subtasks-title" className="grid max-w-md gap-3 rounded-xl border bg-card p-5 shadow-lg">
        <h2 id="board-subtasks-title" className="font-semibold">Complete open subtasks too?</h2>
        <p className="text-sm">“{choice.card.task.title}” has open subtasks.</p>
        <div className="flex flex-wrap gap-2">
          <Button type="button" disabled={disabled} onClick={() => { const pending = choice; setChoice(null); if (pending) void moveCard(pending.card, pending.toColumn, pending.placement, "CompleteAll"); }}>Complete them too</Button>
          <Button type="button" variant="outline" disabled={disabled} onClick={() => { const pending = choice; setChoice(null); if (pending) void moveCard(pending.card, pending.toColumn, pending.placement, "KeepOpen"); }}>Keep them open</Button>
          <Button type="button" variant="ghost" onClick={() => setChoice(null)}>Cancel</Button>
        </div>
      </div>
    </div>}
  </div>;
}

/** The board settings editor: a local draft of the saved settings, saved as one board.updateSettings call. */
function BoardSettingsPanel({ settings, columns, tags, disabled, forView = false, onSave, onCancel }: {
  settings: BoardSettingsDto; forView?: boolean; columns: readonly BoardColumnResponse[]; tags: TagResponse[]; disabled: boolean;
  onSave: (draft: SettingsDraft) => void; onCancel: () => void;
}) {
  const [current, setDraft] = React.useState(() => draftFrom(settings, columns.map(column => column.key)));
  const parents = tags.filter(tag => tags.some(child => child.parentId === tag.id));
  const set = (patch: Partial<SettingsDraft>) => setDraft(draft => ({ ...draft, ...patch }));
  return <div id="board-settings" className="grid gap-4 rounded-xl border bg-muted/50 p-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Group columns by"><NativeSelect value={current.groupBy} disabled={disabled} onChange={e => set({ groupBy: e.target.value })}>
          {(forView ? viewGroupByChoices : groupByChoices).map(([value, name]) => <NativeSelectOption key={value} value={value}>{name}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Field label="Swimlanes"><NativeSelect value={current.swimlaneBy} disabled={disabled} onChange={e => set({ swimlaneBy: e.target.value })}>
          {(forView ? viewSwimlaneChoices : swimlaneChoices).map(([value, name]) => <NativeSelectOption key={value} value={value}>{name}</NativeSelectOption>)}
        </NativeSelect></Field>
        {current.groupBy === "TagGroup" && <Field label="Column parent tag"><NativeSelect value={current.columnTagId} disabled={disabled} onChange={e => set({ columnTagId: e.target.value })}>
          <NativeSelectOption value="">Choose a parent tag…</NativeSelectOption>
          {parents.map(tag => <NativeSelectOption key={tag.id} value={tag.id}>{tag.path}</NativeSelectOption>)}
        </NativeSelect></Field>}
        {current.swimlaneBy === "TagGroup" && <Field label="Swimlane parent tag"><NativeSelect value={current.swimlaneTagId} disabled={disabled} onChange={e => set({ swimlaneTagId: e.target.value })}>
          <NativeSelectOption value="">Choose a parent tag…</NativeSelectOption>
          {parents.map(tag => <NativeSelectOption key={tag.id} value={tag.id}>{tag.path}</NativeSelectOption>)}
        </NativeSelect></Field>}
      </div>
      <fieldset className="grid gap-2 rounded-lg border p-3 pt-2">
        <legend className="px-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">WIP limits and hidden columns</legend>
        <ul className="grid gap-1.5">
          {columns.map(column => <li key={column.key} className="flex flex-wrap items-center gap-3 text-sm">
            <span className="min-w-0 flex-1 truncate">{column.title}</span>
            <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
              WIP limit
              <Input type="number" min={1} max={9999} className="h-7 w-20" aria-label={`WIP limit for ${column.title}`} disabled={disabled}
                value={current.wip[column.key] ?? ""} onChange={e => set({ wip: { ...current.wip, [column.key]: e.target.value } })}/>
            </label>
            <label className="flex items-center gap-1.5 text-xs">
              <input type="checkbox" className="size-4 accent-primary" checked={current.hidden.includes(column.key)} disabled={disabled}
                aria-label={`Hide ${column.title}`} onChange={e => set({ hidden: e.target.checked ? [...current.hidden, column.key] : current.hidden.filter(key => key !== column.key) })}/>
              Hide
            </label>
          </li>)}
        </ul>
      </fieldset>
      <div className="flex gap-2">
        <Button type="button" disabled={disabled || (current.groupBy === "TagGroup" && !current.columnTagId) || (current.swimlaneBy === "TagGroup" && !current.swimlaneTagId)} onClick={() => onSave(current)}>Save board settings</Button>
        <Button type="button" variant="outline" disabled={disabled} onClick={onCancel}>Cancel</Button>
      </div>
    </div>;
}

const layoutChoices = [["list", "List"], ["board", "Board"]] as const;

/** The List / Board switch shared by the list and project pages; never changes tasks. */
export function LayoutSwitch({ layout, onLayout, disabled }: { layout: BoardLayout; onLayout: (layout: BoardLayout) => void; disabled?: boolean }) {
  return <SegmentedRadio label="Layout" value={layout} options={layoutChoices} onChange={onLayout} disabled={disabled}/>;
}
