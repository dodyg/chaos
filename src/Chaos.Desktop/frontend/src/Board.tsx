import React from "react";
import { boardRpc, type BoardCardResponse, type BoardColumnResponse, type BoardLaneResponse, type BoardSettingsDto, type TagResponse, type TaskResponse } from "#neoastra";
import { CalendarDays, CheckSquare, Clock, MoreHorizontal, Settings2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Count, Field, InlineError } from "@/components/common";
import { DateText } from "./dates";
import { GitHubTaskMark } from "./GitHubTaskMark";
import { TagDot } from "./tags";
import { useDragReorder } from "./dragReorder";
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
 * A Kanban board for one list or project (PLAN2 §11.1): columns from the board settings,
 * optional swimlanes, cards dragged or keyboard-moved between and within columns, WIP limit
 * warnings and a settings popover. Every move is one board.move call with the task's version.
 */
export function Board({ containerId, tags, focus, busy, revision, onOpen, onChanged, api = boardRpc }: {
  containerId: string; tags: TagResponse[]; focus: Focus; busy: boolean; revision: number;
  onOpen: (task: TaskResponse, viewing: boolean) => void; onChanged: () => void; api?: BoardApi;
}) {
  const [board, setBoard] = React.useState<{ columns: readonly BoardColumnResponse[]; lanes: readonly BoardLaneResponse[]; cards: readonly BoardCardResponse[]; settings: BoardSettingsDto; totalCount: number; truncated: boolean } | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [showCancelled, setShowCancelled] = React.useState(false);
  const [showSettings, setShowSettings] = React.useState(false);
  const [draft, setDraft] = React.useState<SettingsDraft | null>(null);
  const [moving, setMoving] = React.useState(false);
  const [choice, setChoice] = React.useState<{ card: BoardCardResponse; toColumn: string; toIndex: number | null } | null>(null);
  const [menu, setMenu] = React.useState<{ cardId: string; page: "main" | "columns" } | null>(null);
  const [focusCardId, setFocusCardId] = React.useState<string | null>(null);
  const drag = useDragReorder();
  const cardRefs = React.useRef(new Map<string, HTMLElement>());

  const disabled = busy || moving;

  React.useEffect(() => {
    let current = true;
    void api.get({ containerId, category: focus, showCancelled }).then(result => {
      if (!current) return;
      if (result.ok) { setBoard(result); setError(null); setDraft(null); }
      else setError(result.error?.message ?? "Could not load the board.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, containerId, focus, showCancelled, revision]);

  const settings = board?.settings;
  const hiddenKeys = new Set(parseJson<string[]>(settings?.hiddenColumnsJson, []));
  const wipLimits = parseJson<Record<string, number>>(settings?.wipLimitsJson, {});
  const columns = board ? board.columns.filter(column => !hiddenKeys.has(column.key)) : [];
  const lanes = board?.lanes ?? [{ key: "", title: "All tasks", isNone: true }];
  const multipleLanes = lanes.length > 1;
  const cards = board?.cards ?? [];
  const tagById = React.useMemo(() => new Map(tags.map(tag => [tag.id, tag])), [tags]);
  // Cards of one column in board order (the container's manual order) — the reorder index space.
  const columnCards = React.useMemo(() => {
    const byColumn = new Map<string, BoardCardResponse[]>();
    for (const column of columns) byColumn.set(column.key, cards.filter(card => card.columnKey === column.key));
    return byColumn;
  }, [columns, cards]);

  async function moveCard(card: BoardCardResponse, toColumn: string, toIndex: number | null, subtaskChoice: string | null = null) {
    if (disabled) return;
    setMoving(true);
    try {
      const result = await api.move({
        operationId: operationId(), taskId: card.task.id, expectedVersion: card.task.version,
        groupBy: settings?.groupBy ?? "Status", fromColumn: card.columnKey, toColumn, toIndex, subtaskChoice,
      });
      if (!result.ok) {
        // Moving to Done with open subtasks is a question, not a failure: ask and retry.
        if (result.error?.fields.some(field => field.fieldPath === "openSubtaskChoice")) {
          setChoice({ card, toColumn, toIndex });
          return;
        }
        setError(result.error?.message ?? "Could not move the task.");
        return;
      }
      setNotice(null);
      if (result.warning) setNotice(result.warning);
      onChanged();
    } catch (e) {
      setError(String(e));
    } finally {
      setMoving(false);
    }
  }

  async function saveSettings(next: SettingsDraft) {
    if (disabled || !board) return;
    setMoving(true);
    try {
      const limits = Object.fromEntries(Object.entries(next.wip).filter(([, value]) => value !== "").map(([key, value]) => [key, Number(value)]));
      const result = await api.updateSettings({
        operationId: operationId(), targetKind: "Container", targetId: containerId, expectedVersion: board.settings.version,
        groupBy: next.groupBy, swimlaneBy: next.swimlaneBy,
        columnTagId: next.columnTagId || null, swimlaneTagId: next.swimlaneTagId || null,
        wipLimitsJson: JSON.stringify(limits), hiddenColumnsJson: JSON.stringify(next.hidden),
      });
      if (!result.ok || !result.settings) {
        setError(result.error?.message ?? "Could not save the board settings.");
        return;
      }
      setDraft(null);
      setShowSettings(false);
      onChanged();
    } catch (e) {
      setError(String(e));
    } finally {
      setMoving(false);
    }
  }

  function focusCard(id: string | undefined) {
    if (!id) return;
    setFocusCardId(id);
    cardRefs.current.get(id)?.focus();
  }

  function cardKeyDown(event: React.KeyboardEvent<HTMLElement>, card: BoardCardResponse) {
    if (event.target !== event.currentTarget) return; // the title button and menu handle their own keys
    const columnList = columnCards.get(card.columnKey) ?? [];
    const index = columnList.findIndex(item => item.task.id === card.task.id);
    const columnIndex = columns.findIndex(column => column.key === card.columnKey);
    switch (event.key) {
      case "ArrowDown":
      case "ArrowUp": {
        event.preventDefault();
        const next = columnList[index + (event.key === "ArrowDown" ? 1 : -1)];
        focusCard(next?.task.id);
        break;
      }
      case "ArrowRight":
      case "ArrowLeft": {
        event.preventDefault();
        const nextColumn = columns[columnIndex + (event.key === "ArrowRight" ? 1 : -1)];
        if (!nextColumn) return;
        const nextList = columnCards.get(nextColumn.key) ?? [];
        focusCard((nextList[Math.min(index, nextList.length - 1)] ?? nextList[0])?.task.id);
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
    const columnList = columnCards.get(card.columnKey) ?? [];
    const index = columnList.findIndex(item => item.task.id === card.task.id);
    const item = "flex w-full cursor-default rounded-sm px-2 py-1.5 text-left outline-none hover:bg-accent focus-visible:bg-accent focus-visible:outline-none disabled:opacity-50";
    return <div role="menu" aria-label={`Actions for ${card.task.title}`} className="absolute top-full right-0 z-30 mt-1 grid min-w-48 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-lg"
      onKeyDown={event => { if (event.key === "Escape") setMenu(null); }}>
      {menu.page === "main" ? <>
        <button type="button" role="menuitem" className={item} disabled={columns.length < 2} onClick={() => setMenu({ cardId: card.task.id, page: "columns" })}>Move to column…</button>
        <button type="button" role="menuitem" className={item} disabled={disabled || index <= 0} onClick={() => { setMenu(null); void moveCard(card, card.columnKey, index - 1); }}>Move up</button>
        <button type="button" role="menuitem" className={item} disabled={disabled || index >= columnList.length - 1} onClick={() => { setMenu(null); void moveCard(card, card.columnKey, index + 1); }}>Move down</button>
      </> : <>
        {columns.filter(column => column.key !== card.columnKey).map(column => <button key={column.key} type="button" role="menuitem" className={item} disabled={disabled} onClick={() => { setMenu(null); void moveCard(card, column.key, null); }}>Move to {column.title}</button>)}
        <button type="button" role="menuitem" className={item} onClick={() => setMenu({ cardId: card.task.id, page: "main" })}>Back</button>
      </>}
    </div>;
  }

  function renderCard(card: BoardCardResponse, order: readonly string[], index: number) {
    const task = card.task;
    return <li key={task.id}
      ref={element => { if (element) cardRefs.current.set(task.id, element); else cardRefs.current.delete(task.id); }}
      tabIndex={focusCardId === null || focusCardId === task.id ? 0 : -1}
      aria-label={task.title}
      className={cn("group/card relative grid gap-1 rounded-lg border bg-card p-2 shadow-xs outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50", moving && "opacity-60")}
      onKeyDown={event => cardKeyDown(event, card)}
      {...drag.target(order, index, `${card.laneKey}::${card.columnKey}`, disabled, (id, insertionIndex) => {
        const dropped = cards.find(item => item.task.id === id);
        if (dropped && !(dropped.columnKey === card.columnKey && insertionIndex === order.indexOf(id))) void moveCard(dropped, card.columnKey, insertionIndex);
      }, { foreign: true })}>
      <div className="flex items-start">
        <CategoryEdge category={task.category}/>
        {drag.handle(task.id, `${card.laneKey}::${card.columnKey}`, disabled, "py-0.5")}
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
    const wholeColumn = columnCards.get(column.key) ?? [];
    const inLane = cards.filter(card => card.columnKey === column.key && card.laneKey === lane.key);
    const limit = wipLimits[column.key];
    const over = limit !== undefined && wholeColumn.length > limit;
    return <section key={`${lane.key}|${column.key}`} aria-label={`${lane.title} · ${column.title} column`}
      className={cn("flex w-64 shrink-0 grid-cols-1 flex-col gap-1.5 rounded-lg bg-muted/40 p-2 outline-none", menu === null && "focus-within:ring-1 focus-within:ring-primary/40")}
      {...drag.zone(disabled, id => {
        const dropped = cards.find(card => card.task.id === id);
        if (dropped && dropped.columnKey !== column.key) void moveCard(dropped, column.key, null);
      })}>
      <h4 className="flex items-center gap-1.5 px-1 text-xs font-semibold tracking-wider text-muted-foreground uppercase select-none">
        <span className="min-w-0 truncate">{column.title}</span>
        <span className="tabular-nums">{limit !== undefined
          ? <span className={over ? "font-medium text-amber-600 dark:text-amber-400" : undefined}>{wholeColumn.length} / {limit}</span>
          : wholeColumn.length}</span>
        {over && <span className="sr-only">over its WIP limit</span>}
      </h4>
      <ul className="grid content-start gap-1.5" aria-label={`${column.title} cards`}>
        {inLane.map(card => renderCard(card, wholeColumn.map(item => item.task.id), wholeColumn.findIndex(item => item.task.id === card.task.id)))}
      </ul>
      {inLane.length === 0 && <p className="px-1 py-2 text-xs text-muted-foreground italic">No cards.</p>}
    </section>;
  }

  function settingsPanel() {
    if (!showSettings || !board) return null;
    const current = draft ?? draftFrom(board.settings, board.columns.map(column => column.key));
    const parents = tags.filter(tag => tags.some(child => child.parentId === tag.id));
    const set = (patch: Partial<SettingsDraft>) => setDraft({ ...current, ...patch });
    return <div id="board-settings" className="grid gap-4 rounded-xl border bg-muted/50 p-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Group columns by"><NativeSelect value={current.groupBy} disabled={disabled} onChange={e => set({ groupBy: e.target.value })}>
          {groupByChoices.map(([value, name]) => <NativeSelectOption key={value} value={value}>{name}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Field label="Swimlanes"><NativeSelect value={current.swimlaneBy} disabled={disabled} onChange={e => set({ swimlaneBy: e.target.value })}>
          {swimlaneChoices.map(([value, name]) => <NativeSelectOption key={value} value={value}>{name}</NativeSelectOption>)}
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
          {board.columns.map(column => <li key={column.key} className="flex flex-wrap items-center gap-3 text-sm">
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
        <Button type="button" disabled={disabled || (current.groupBy === "TagGroup" && !current.columnTagId) || (current.swimlaneBy === "TagGroup" && !current.swimlaneTagId)} onClick={() => void saveSettings(current)}>Save board settings</Button>
        <Button type="button" variant="outline" disabled={disabled} onClick={() => { setDraft(null); setShowSettings(false); }}>Cancel</Button>
      </div>
    </div>;
  }

  return <div className="grid gap-3" aria-label="Board">
    <div className="flex flex-wrap items-center gap-3">
      <Button type="button" variant={showSettings ? "secondary" : "outline"} disabled={disabled} aria-expanded={showSettings} aria-controls="board-settings" onClick={() => { setShowSettings(v => !v); setDraft(null); }}><Settings2/>{showSettings ? "Hide settings" : "Board settings"}</Button>
      {settings?.groupBy === "Status" && <label className="flex items-center gap-1.5 text-xs">
        <input type="checkbox" className="size-4 accent-primary" checked={showCancelled} disabled={disabled} onChange={e => setShowCancelled(e.target.checked)}/>
        Show cancelled
      </label>}
      {board && board.truncated && <p role="status" className="text-xs text-muted-foreground">{board.cards.length} of {board.totalCount} shown, filter to see more</p>}
    </div>
    {settingsPanel()}
    {error && <InlineError>{error}</InlineError>}
    {notice && <div role="status" className="rounded-lg border bg-muted/50 px-3 py-2 text-sm text-muted-foreground">{notice}</div>}
    {board && !error && <div className="-mx-1 overflow-x-auto px-1 pb-1">
      <div className="grid w-max min-w-full gap-4">
        {lanes.map(lane => <section key={lane.key} aria-label={multipleLanes ? `${lane.title} swimlane` : "Board lanes"} className="grid gap-1.5">
          {multipleLanes && <h3 className="flex items-center px-1 text-xs font-semibold tracking-wider text-muted-foreground uppercase">{lane.title}<Count>{cards.filter(card => card.laneKey === lane.key).length}</Count></h3>}
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
          <Button type="button" disabled={disabled} onClick={() => { const pending = choice; setChoice(null); if (pending) void moveCard(pending.card, pending.toColumn, pending.toIndex, "CompleteAll"); }}>Complete them too</Button>
          <Button type="button" variant="outline" disabled={disabled} onClick={() => { const pending = choice; setChoice(null); if (pending) void moveCard(pending.card, pending.toColumn, pending.toIndex, "KeepOpen"); }}>Keep them open</Button>
          <Button type="button" variant="ghost" onClick={() => setChoice(null)}>Cancel</Button>
        </div>
      </div>
    </div>}
  </div>;
}


/** The List / Board switch shared by the list and project pages; never changes tasks. */
export function LayoutSwitch({ layout, onLayout, disabled }: { layout: BoardLayout; onLayout: (layout: BoardLayout) => void; disabled?: boolean }) {
  return <div role="radiogroup" aria-label="Layout" className="inline-flex rounded-md border bg-muted/40 p-0.5 text-xs">
    {([["list", "List"], ["board", "Board"]] as const).map(([value, name]) => <button key={value} type="button" role="radio" aria-checked={layout === value} disabled={disabled}
      className={cn("rounded-[5px] px-2.5 py-1 font-medium text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50", layout === value && "bg-background text-foreground shadow-xs")}
      onClick={() => onLayout(value)}>{name}</button>)}
  </div>;
}
