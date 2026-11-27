import React from "react";
import { TaskStaleness } from "./TaskStaleness";
import FullCalendar from "@fullcalendar/react";
import dayGridPlugin from "@fullcalendar/daygrid";
import timeGridPlugin from "@fullcalendar/timegrid";
import listPlugin from "@fullcalendar/list";
import interactionPlugin, { Draggable } from "@fullcalendar/interaction";
import type { DatesSetArg, EventClickArg, EventDropArg, EventContentArg, DayHeaderContentArg, DayCellContentArg } from "@fullcalendar/core";
import {
  calendarRpc,
  calendarsRpc,
  markdownRpc,
  taskRpc,
  type CalendarDayDto,
  type CalendarMeetingDto,
  type CalendarEntryDto,
  type CalendarFiltersDto,
  type ContainerResponse,
  type QueryRowResponse,
  type TagResponse,
  type TaskResponse,
} from "#neoastra";
import { ChevronLeft, ChevronRight, Flag, GripVertical, Repeat, Video, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, InlineError, SegmentedRadio } from "@/components/common";
import { friendlyDate } from "./dates";
import { minutesText } from "./MyWeek";
import type { Focus } from "./category";
import { busyText, meetingLink, meetingTime } from "./meetings";
import { useAvailableSpaces } from "./Sharing";

const operationId = () => crypto.randomUUID();

type CalendarApi = typeof calendarRpc;
type TaskApi = Pick<typeof taskRpc, "get" | "edit">;
type CalendarsApi = Pick<typeof calendarsRpc, "makeTask">;
export type CalendarMode = "day" | "week" | "month" | "agenda";
const viewTypes: Record<CalendarMode, string> = { day: "timeGridDay", week: "timeGridWeek", month: "dayGridMonth", agenda: "agenda" };
const modes: readonly (readonly [CalendarMode, string])[] = [
  ["day", "Day"],
  ["week", "Week"],
  ["month", "Month"],
  ["agenda", "Agenda"],
];

/** yyyy-MM-dd of a Date's local calendar day. */
export const localDate = (date: Date) =>
  `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
const localTime = (date: Date) => `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
/** Adds whole days to a yyyy-MM-dd date (calendar arithmetic, no time zone involved). */
export function addDays(date: string, days: number) {
  const [y, m, d] = date.split("-").map(Number);
  const next = new Date(Date.UTC(y, m - 1, d + days));
  return `${next.getUTCFullYear()}-${String(next.getUTCMonth() + 1).padStart(2, "0")}-${String(next.getUTCDate()).padStart(2, "0")}`;
}

/** A calendar entry as FullCalendar's event input: planned and repeat entries can sit at a time; due entries are always markers. */
export function toEvent(entry: CalendarEntryDto) {
  const timed = entry.time !== null && entry.kind !== "Due";
  return {
    id: `${entry.kind}:${entry.taskId}:${entry.date}`,
    title: entry.title,
    start: timed ? `${entry.date}T${entry.time}:00` : entry.date,
    end: timed && entry.endTime ? `${entry.date}T${entry.endTime}:00` : undefined,
    allDay: !timed,
    editable: entry.kind === "Planned",
    durationEditable: false,
    classNames: [
      "chaos-event",
      `chaos-event-${entry.kind.toLowerCase()}`,
      entry.isBlock && "chaos-event-block",
      entry.status === "Done" && "chaos-event-done",
      entry.category === "Work" ? "chaos-event-work" : "",
    ].filter((name): name is string => Boolean(name)),
    extendedProps: { entry },
  };
}

/** Black or white text, whichever reads better on a calendar's own colour (WCAG relative luminance). A colour that is not #rrggbb gets white. */
export function readableTextOn(color: string | null | undefined): string {
  const match = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(color ?? "");
  if (!match) return "#ffffff";
  const [r, g, b] = [match[1], match[2], match[3]].map((part) => {
    const channel = parseInt(part, 16) / 255;
    return channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  // Choose the text with the higher contrast ratio against this background.
  return (luminance + 0.05) / 0.05 > 1.05 / (luminance + 0.05) ? "#111111" : "#ffffff";
}

/** A meeting as FullCalendar input: read-only, in its calendar's colour, lighter when it is free time. */
export function toMeetingEvent(meeting: CalendarMeetingDto) {
  const timed = !meeting.allDay && meeting.start !== null;
  return {
    id: `Meeting:${meeting.id}`,
    title: meeting.title,
    start: timed ? `${meeting.date}T${meeting.start}:00` : meeting.date,
    end: timed && meeting.end ? `${meeting.date}T${meeting.end}:00` : undefined,
    allDay: !timed,
    editable: false,
    startEditable: false,
    durationEditable: false,
    backgroundColor: meeting.color ?? undefined,
    borderColor: meeting.color ?? undefined,
    textColor: readableTextOn(meeting.color),
    classNames: ["chaos-event", "chaos-event-meeting", !meeting.busy && "chaos-event-free"].filter((name): name is string => Boolean(name)),
    extendedProps: { meeting },
  };
}

/** The unfinished-planning notes a day header or cell shows: only days over their time get a badge. */
export function dayBadge(day: CalendarDayDto | undefined) {
  if (!day || !(day.overTotal || day.overWork)) return null;
  const reason = day.overTotal
    ? `${minutesText(day.plannedMinutes)} planned in ${minutesText(day.availableMinutes)}`
    : `${minutesText(day.workMinutes)} of work in ${minutesText(day.workAllowanceMinutes ?? 0)}`;
  return { text: day.overTotal ? "Over" : "Over work", reason };
}

function DayBadge({ day }: { day: CalendarDayDto | undefined }) {
  const badge = dayBadge(day);
  const busy =
    day && day.busyMinutes > 0 ? (
      <span data-busy={day.date} className="ml-1 text-[0.65rem] font-normal text-muted-foreground">
        {busyText(day.busyMinutes)}
      </span>
    ) : null;
  if (!badge) return busy;
  return (
    <>
      {busy}
      <span
        data-overload={day!.date}
        title={badge.reason}
        aria-label={`Overloaded: ${badge.reason}`}
        className="ml-1 inline-flex rounded-full bg-destructive/15 px-1.5 text-[0.65rem] font-semibold text-destructive"
      >
        {badge.text}
      </span>
    </>
  );
}

/** The date and optional time a task is planned for; used from the entry menu and the unscheduled panel (the keyboard way to move). */
export function MoveDialog({
  title,
  heading,
  initialDate,
  initialTime,
  error,
  busy,
  onSave,
  onCancel,
}: {
  title: string;
  heading: string;
  initialDate: string;
  initialTime: string | null;
  error: string | null;
  busy: boolean;
  onSave: (date: string, time: string | null) => void;
  onCancel: () => void;
}) {
  const [date, setDate] = React.useState(initialDate);
  const [time, setTime] = React.useState(initialTime ?? "");
  const ref = React.useRef<HTMLInputElement>(null);
  React.useEffect(() => {
    ref.current?.focus();
  }, []);
  return (
    <form
      role="dialog"
      aria-label={heading}
      className="grid gap-3 rounded-xl border bg-card p-3 shadow-sm"
      onKeyDown={(event) => {
        if (event.key === "Escape") {
          event.stopPropagation();
          onCancel();
        }
      }}
      onSubmit={(event) => {
        event.preventDefault();
        if (date) onSave(date, time || null);
      }}
    >
      <p className="text-sm font-medium">
        <span className="text-muted-foreground">{heading}: </span>
        {title}
      </p>
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Planned date">
          <Input ref={ref} type="date" required value={date} onChange={(event) => setDate(event.target.value)} />
        </Field>
        <Field label="Time (optional)">
          <Input type="time" value={time} onChange={(event) => setTime(event.target.value)} />
        </Field>
        <Button type="submit" size="sm" disabled={busy || !date}>
          Save
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
      {error && <InlineError>{error}</InlineError>}
    </form>
  );
}

type Selected = { entry: CalendarEntryDto };
type SelectedMeeting = { meeting: CalendarMeetingDto };
type Moving = { taskId: string; title: string; date: string; time: string | null };

/** The side panel of open tasks with no planned date: dragged onto the grid or planned with the keyboard. */
export function UnscheduledPanel({
  filters,
  revision,
  onOpen,
  onPlan,
  api,
  listRef,
  onTotal,
  onClose,
}: {
  filters: CalendarFiltersDto;
  revision: number;
  onOpen: (taskId: string) => void;
  onPlan: (row: QueryRowResponse) => void;
  api: CalendarApi;
  listRef: React.RefObject<HTMLDivElement | null>;
  onTotal?: (total: number) => void;
  onClose?: () => void;
}) {
  const [rows, setRows] = React.useState<QueryRowResponse[]>([]);
  const [total, setTotal] = React.useState(0);
  const [cursor, setCursor] = React.useState<string | null>(null);
  const [text, setText] = React.useState("");
  const [query, setQuery] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(false);
  const filterKey = JSON.stringify(filters);

  React.useEffect(() => {
    const timer = window.setTimeout(() => setQuery(text.trim()), 250);
    return () => window.clearTimeout(timer);
  }, [text]);

  React.useEffect(() => {
    let current = true;
    setLoading(true);
    void api
      .unscheduled({ filters, text: query || null, cursor: null, limit: 25 })
      .then((result) => {
        if (!current) return;
        if (result.ok) {
          setRows([...result.rows]);
          setTotal(result.totalCount);
          onTotal?.(result.totalCount);
          setCursor(result.nextCursor);
          setError(null);
        } else setError(result.error?.message ?? "Could not load the unscheduled tasks.");
        setLoading(false);
      })
      .catch((e: unknown) => {
        if (current) {
          setError(String(e));
          setLoading(false);
        }
      });
    return () => {
      current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- filterKey stands for filters
  }, [api, filterKey, query, revision]);

  async function more() {
    if (!cursor || loading) return;
    setLoading(true);
    try {
      const result = await api.unscheduled({ filters, text: query || null, cursor, limit: 25 });
      if (result.ok) {
        setRows((current) => [...current, ...result.rows]);
        setCursor(result.nextCursor);
      } else setError(result.error?.message ?? "Could not load more.");
    } catch (e) {
      setError(String(e));
    }
    setLoading(false);
  }

  return (
    <aside
      aria-label="Unscheduled tasks"
      className="grid content-start gap-2 rounded-xl border bg-popover p-3 text-popover-foreground shadow-lg"
      onKeyDown={(event) => {
        if (event.key === "Escape" && onClose) {
          event.stopPropagation();
          onClose();
        }
      }}
    >
      <h3 className="flex items-center justify-between gap-2 text-sm font-semibold">
        <span className="flex items-baseline gap-2">
          Unscheduled<span className="text-xs font-normal text-muted-foreground tabular-nums">{total}</span>
        </span>
        {onClose && (
          <Button type="button" size="icon-xs" variant="ghost" aria-label="Close unscheduled tasks" onClick={onClose}>
            <X />
          </Button>
        )}
      </h3>
      <label className="sr-only" htmlFor="unscheduled-filter">
        Filter unscheduled tasks
      </label>
      <Input id="unscheduled-filter" type="search" placeholder="Filter" value={text} onChange={(event) => setText(event.target.value)} />
      {error && <InlineError>{error}</InlineError>}
      <div ref={listRef} className="grid max-h-[60vh] gap-1 overflow-y-auto overflow-x-hidden">
        {rows.map((row) => (
          <div
            key={row.task.id}
            data-unscheduled-id={row.task.id}
            data-title={row.task.title}
            className="flex min-w-0 items-center gap-1 rounded-md border bg-card px-1.5 py-1 text-sm"
          >
            <GripVertical className="size-3.5 shrink-0 cursor-grab text-muted-foreground" aria-hidden="true" />
            <button
              type="button"
              className="min-w-0 flex-1 truncate text-left outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50"
              onClick={() => onOpen(row.task.id)}
            >
              {row.task.title}
            </button>
            <TaskStaleness task={row.task} lastActivityAtUtc={row.lastActivityAtUtc} revision={revision} />
            <Button
              type="button"
              size="xs"
              variant="ghost"
              className="shrink-0"
              aria-label={`Plan “${row.task.title}”`}
              onClick={() => onPlan(row)}
            >
              Plan…
            </Button>
          </div>
        ))}
        {rows.length === 0 && !loading && (
          <p className="text-sm text-muted-foreground">{query ? "No unscheduled task matches." : "Every open task has a planned date."}</p>
        )}
        {cursor && (
          <Button type="button" size="sm" variant="outline" disabled={loading} onClick={() => void more()}>
            Show more
          </Button>
        )}
      </div>
      <p className="text-xs text-muted-foreground">Drag a task onto a day or a time slot, or choose Plan….</p>
    </aside>
  );
}

/**
 * The calendar (PLAN2 §10.1): Day, Week, Month and Agenda over FullCalendar, with planned blocks,
 * due markers, projected repeats, per-day overload badges and the unscheduled panel. Dragging changes
 * only the planned date (and time) through the ordinary task edit, so it is undoable; the entry menu's
 * "Move to date…" is the keyboard way. `viewId` limits the calendar to one custom view's tasks.
 */
export function Calendar({
  viewId,
  tags,
  containers,
  focus,
  busy,
  revision,
  onOpen,
  onChanged,
  api = calendarRpc,
  taskApi = taskRpc,
  calendarsApi = calendarsRpc,
  openExternal = (url: string) => void markdownRpc.openExternal({ url }),
  today: todayProp,
}: {
  viewId?: string;
  tags: TagResponse[];
  containers: ContainerResponse[];
  focus: Focus;
  busy: boolean;
  revision: number;
  onOpen: (task: TaskResponse) => void;
  onChanged: () => void;
  api?: CalendarApi;
  taskApi?: TaskApi;
  calendarsApi?: CalendarsApi;
  openExternal?: (url: string) => void;
  today?: string;
}) {
  const calendarRef = React.useRef<FullCalendar>(null);
  const regionRef = React.useRef<HTMLDivElement>(null);
  const unscheduledRef = React.useRef<HTMLDivElement>(null);
  const [mode, setMode] = React.useState<CalendarMode>(() => {
    try {
      const stored = localStorage.getItem("chaos.calendarMode");
      return stored === "day" || stored === "month" || stored === "agenda" ? stored : "week";
    } catch {
      return "week";
    }
  });
  const [range, setRange] = React.useState<{ from: string; to: string; title: string } | null>(null);
  const [data, setData] = React.useState<{
    entries: readonly CalendarEntryDto[];
    meetings: readonly CalendarMeetingDto[];
    days: ReadonlyMap<string, CalendarDayDto>;
    today: string;
    zoneId: string;
    truncated: boolean;
  } | null>(null);
  const [showPlanned, setShowPlanned] = React.useState(true);
  const [showDue, setShowDue] = React.useState(true);
  const [showMeetings, setShowMeetings] = React.useState(true);
  const [showCompleted, setShowCompleted] = React.useState(false);
  const spaces = useAvailableSpaces();
  const [spaceId, setSpaceId] = React.useState("");
  const [tagId, setTagId] = React.useState("");
  const [containerId, setContainerId] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [moveError, setMoveError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);
  const [reloads, setReloads] = React.useState(0);
  const [selected, setSelected] = React.useState<Selected | null>(null);
  const [moving, setMoving] = React.useState<Moving | null>(null);
  const [meeting, setMeeting] = React.useState<SelectedMeeting | null>(null);
  const [notice, setNotice] = React.useState<{ text: string; task: TaskResponse } | null>(null);
  const [cursorDate, setCursorDate] = React.useState<string | null>(null);
  const [showUnscheduled, setShowUnscheduled] = React.useState(false);
  const [unscheduledTotal, setUnscheduledTotal] = React.useState(0);

  const filters = React.useMemo<CalendarFiltersDto>(
    () => ({
      category: focus,
      spaceId: spaceId || null,
      tagId: tagId || null,
      containerId: containerId || null,
      viewId: viewId ?? null,
      showPlanned,
      showDue,
      showMeetings,
      showCompleted,
    }),
    [focus, spaceId, tagId, containerId, viewId, showPlanned, showDue, showMeetings, showCompleted],
  );
  const today = data?.today ?? todayProp ?? localDate(new Date());

  React.useEffect(() => {
    try {
      localStorage.setItem("chaos.calendarMode", mode);
    } catch {
      /* storage unavailable */
    }
  }, [mode]);

  React.useEffect(() => {
    if (!range) return;
    let current = true;
    void api
      .range({ from: range.from, to: range.to, filters })
      .then((result) => {
        if (!current) return;
        if (result.ok) {
          setData({
            entries: result.entries,
            meetings: result.meetings,
            days: new Map(result.days.map((day) => [day.date, day])),
            today: result.today,
            zoneId: result.zoneId,
            truncated: result.truncated,
          });
          setError(null);
        } else setError(result.error?.message ?? "Could not load the calendar.");
      })
      .catch((e: unknown) => {
        if (current) setError(String(e));
      });
    return () => {
      current = false;
    };
  }, [api, range, filters, revision, reloads]);

  // Dragging a row of the unscheduled panel onto the grid plans it; the panel's own rows are the drag sources.
  React.useEffect(() => {
    const element = unscheduledRef.current;
    if (!element) return;
    const draggable = new Draggable(element, {
      itemSelector: "[data-unscheduled-id]",
      eventData: (el) => ({ title: el.dataset.title ?? "", create: false }),
    });
    return () => draggable.destroy();
  }, []);

  const events = React.useMemo(
    () => [...(data?.entries ?? []).map(toEvent), ...(showMeetings ? (data?.meetings ?? []) : []).map(toMeetingEvent)],
    [data, showMeetings],
  );

  async function makeTask(picked: CalendarMeetingDto) {
    setSaving(true);
    try {
      const result = await calendarsApi.makeTask({
        operationId: operationId(),
        sourceId: picked.sourceId,
        eventId: picked.eventId,
        containerId: null,
      });
      if (!result.ok || !result.task) {
        setError(result.error?.message ?? "The task could not be made.");
        return;
      }
      setError(null);
      setNotice({ text: `Made “${result.task.title}”, planned for ${result.task.plannedDate ?? "later"}.`, task: result.task });
      setMeeting(null);
      setReloads((count) => count + 1);
      onChanged();
    } catch (e) {
      setError(String(e));
    } finally {
      setSaving(false);
    }
  }

  async function openTask(taskId: string) {
    try {
      const result = await taskApi.get({ taskId });
      if (result.ok && result.task) onOpen(result.task);
      else setError(result.error?.message ?? "That task could not be opened.");
    } catch (e) {
      setError(String(e));
    }
  }

  /** The one write the calendar makes: the task's planned date and time through the ordinary edit (undoable). */
  async function plan(taskId: string, date: string, time: string | null): Promise<boolean> {
    setSaving(true);
    try {
      const got = await taskApi.get({ taskId });
      if (!got.ok || !got.task) {
        setError(got.error?.message ?? "That task no longer exists.");
        return false;
      }
      const task = got.task;
      const result = await taskApi.edit({
        operationId: operationId(),
        taskId,
        expectedVersion: task.version,
        title: task.title,
        notes: task.notes,
        plannedDate: date,
        plannedTime: time,
        plannedZoneId: time ? (task.plannedZoneId ?? data?.zoneId ?? null) : null,
        dueDate: task.dueDate,
        dueTime: task.dueDate ? task.dueTime : null,
        dueZoneId: task.dueDate ? task.dueZoneId : null,
        priority: task.priority,
        estimateMinutes: task.estimateMinutes,
        category: task.category,
      });
      if (!result.ok) {
        setError(result.error?.message ?? "The task could not be planned.");
        return false;
      }
      setError(null);
      setReloads((count) => count + 1);
      onChanged();
      return true;
    } catch (e) {
      setError(String(e));
      return false;
    } finally {
      setSaving(false);
    }
  }

  async function eventDrop(info: EventDropArg) {
    const entry = info.event.extendedProps.entry as CalendarEntryDto | undefined;
    const start = info.event.start;
    if (!entry || entry.kind !== "Planned" || !start) {
      info.revert();
      return;
    }
    // Dropping onto the all-day row clears the time; a time slot sets it. Due dates are never touched.
    if (!(await plan(entry.taskId, localDate(start), info.event.allDay ? null : localTime(start)))) info.revert();
  }

  async function dropFromPanel(info: { date: Date; allDay: boolean; draggedEl: HTMLElement }) {
    const taskId = info.draggedEl.dataset.unscheduledId;
    if (taskId) await plan(taskId, localDate(info.date), info.allDay ? null : localTime(info.date));
  }

  function eventClick(info: EventClickArg) {
    info.jsEvent?.preventDefault();
    const clicked = info.event.extendedProps.meeting as CalendarMeetingDto | undefined;
    if (clicked) {
      setMeeting({ meeting: clicked });
      setSelected(null);
      setMoving(null);
      return;
    }
    const entry = info.event.extendedProps.entry as CalendarEntryDto | undefined;
    if (!entry) return;
    // A repeat or a deadline opens its task straight away; a planned entry offers the menu (open or move).
    if (entry.kind === "Planned") {
      setSelected({ entry });
      setMoving(null);
    } else void openTask(entry.taskId);
  }

  function datesSet(info: DatesSetArg) {
    const from = localDate(info.start),
      to = addDays(localDate(info.end), -1);
    setRange((current) =>
      current && current.from === from && current.to === to && current.title === info.view.title
        ? current
        : { from, to, title: info.view.title },
    );
  }

  const api$ = () => calendarRef.current?.getApi();
  function switchMode(next: CalendarMode) {
    setMode(next);
    api$()?.changeView(viewTypes[next]);
  }
  function go(date: string) {
    setCursorDate(date);
    api$()?.gotoDate(date);
  }

  /** Arrow keys move between days (up and down by a week), Enter opens the day; only when the grid itself has focus. */
  function onGridKeys(event: React.KeyboardEvent<HTMLDivElement>) {
    if (event.target !== event.currentTarget) return;
    const step =
      event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : event.key === "ArrowUp" ? -7 : event.key === "ArrowDown" ? 7 : 0;
    if (step !== 0) {
      event.preventDefault();
      const next = addDays(cursorDate ?? today, step);
      setCursorDate(next);
      if (range && (next < range.from || next > range.to)) api$()?.gotoDate(next);
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      const day = cursorDate ?? today;
      setMode("day");
      api$()?.changeView("timeGridDay", day);
    }
  }

  const flatTags = [...tags].sort((a, b) => a.path.localeCompare(b.path));
  const days = data?.days;
  const selectedEntry = selected?.entry;
  const cursorClass = (date: Date) => (cursorDate !== null && localDate(date) === cursorDate ? ["chaos-cursor"] : []);

  return (
    <section aria-label="Calendar" className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" size="sm" variant="outline" onClick={() => go(today)}>
          Today
        </Button>
        <Button type="button" size="icon-sm" variant="ghost" aria-label="Previous" onClick={() => api$()?.prev()}>
          <ChevronLeft />
        </Button>
        <Button type="button" size="icon-sm" variant="ghost" aria-label="Next" onClick={() => api$()?.next()}>
          <ChevronRight />
        </Button>
        <h3 className="text-lg font-semibold" aria-live="polite">
          {range?.title ?? ""}
        </h3>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <label className="sr-only" htmlFor="calendar-goto">
            Go to date
          </label>
          <Input
            id="calendar-goto"
            type="date"
            className="h-8 w-40"
            value={cursorDate ?? ""}
            onChange={(event) => {
              if (event.target.value) go(event.target.value);
            }}
          />
          <Button
            type="button"
            size="sm"
            variant="outline"
            aria-expanded={showUnscheduled}
            onClick={() => setShowUnscheduled((value) => !value)}
          >
            Unscheduled
            <span className="text-xs text-muted-foreground tabular-nums">{unscheduledTotal}</span>
          </Button>
          <SegmentedRadio label="Calendar view" value={mode} options={modes} onChange={switchMode} />
        </div>
      </div>

      <fieldset className="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm" aria-label="Calendar filters">
        <label className="inline-flex items-center gap-1.5">
          <input type="checkbox" checked={showPlanned} onChange={(event) => setShowPlanned(event.target.checked)} />
          Planned
        </label>
        <label className="inline-flex items-center gap-1.5">
          <input type="checkbox" checked={showDue} onChange={(event) => setShowDue(event.target.checked)} />
          Due
        </label>
        <label className="inline-flex items-center gap-1.5">
          <input type="checkbox" checked={showMeetings} onChange={(event) => setShowMeetings(event.target.checked)} />
          Meetings
        </label>
        <label className="inline-flex items-center gap-1.5">
          <input type="checkbox" checked={showCompleted} onChange={(event) => setShowCompleted(event.target.checked)} />
          Completed
        </label>
        <label className="inline-flex items-center gap-1.5">
          Space
          <NativeSelect aria-label="Space" value={spaceId} onChange={(event) => setSpaceId(event.target.value)}>
            <NativeSelectOption value="">All spaces</NativeSelectOption>
            {spaces.map((space) => (
              <NativeSelectOption key={space.spaceId} value={space.spaceId}>
                {space.name}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </label>
        <label className="inline-flex items-center gap-1.5">
          Tag
          <NativeSelect aria-label="Tag" value={tagId} onChange={(event) => setTagId(event.target.value)}>
            <NativeSelectOption value="">Any tag</NativeSelectOption>
            {flatTags.map((tag) => (
              <NativeSelectOption key={tag.id} value={tag.id}>
                {tag.path}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </label>
        <label className="inline-flex items-center gap-1.5">
          List
          <NativeSelect aria-label="List" value={containerId} onChange={(event) => setContainerId(event.target.value)}>
            <NativeSelectOption value="">Any list or project</NativeSelectOption>
            {containers.map((c) => (
              <NativeSelectOption key={c.id} value={c.id}>
                {c.name}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </label>
      </fieldset>

      {error && <InlineError>{error}</InlineError>}
      {data?.truncated && (
        <p role="status" className="text-sm text-muted-foreground">
          Too many entries to show; narrow the range or the filters.
        </p>
      )}

      {notice && (
        <p role="status" className="flex flex-wrap items-center gap-2 text-sm">
          {notice.text}
          <Button type="button" size="xs" variant="outline" onClick={() => onOpen(notice.task)}>
            Open task
          </Button>
          <Button type="button" size="xs" variant="ghost" onClick={() => setNotice(null)}>
            Dismiss
          </Button>
        </p>
      )}
      {meeting && (
        <div
          role="region"
          aria-label="Selected meeting"
          className="flex flex-wrap items-center gap-2 rounded-xl border border-l-4 bg-card p-3 text-sm"
          style={{ borderLeftColor: meeting.meeting.color ?? undefined }}
        >
          <span className="font-medium">{meeting.meeting.title}</span>
          <span className="text-muted-foreground">
            {friendlyDate(meeting.meeting.date, today)}, {meetingTime(meeting.meeting)}
            {meeting.meeting.location ? ` · ${meeting.meeting.location}` : ""} · {meeting.meeting.sourceName}
            {meeting.meeting.busy ? "" : " · free time"}
          </span>
          <span className="ml-auto flex gap-2">
            {(() => {
              const link = meetingLink(meeting.meeting);
              return link ? (
                <Button type="button" size="sm" variant="outline" onClick={() => openExternal(link)}>
                  <Video />
                  {meeting.meeting.videoUrl ? "Join" : "Open in browser"}
                </Button>
              ) : null;
            })()}
            <Button type="button" size="sm" variant="outline" disabled={busy || saving} onClick={() => void makeTask(meeting.meeting)}>
              Make a task
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setMeeting(null)}>
              Close
            </Button>
          </span>
        </div>
      )}

      {selectedEntry && !moving && (
        <div role="region" aria-label="Selected entry" className="flex flex-wrap items-center gap-2 rounded-xl border bg-card p-3 text-sm">
          <span className="font-medium">{selectedEntry.title}</span>
          <span className="text-muted-foreground">
            {friendlyDate(selectedEntry.date, today)}
            {selectedEntry.time ? ` at ${selectedEntry.time}` : ""}
          </span>
          <span className="ml-auto flex gap-2">
            <Button type="button" size="sm" variant="outline" onClick={() => void openTask(selectedEntry.taskId)}>
              Open task
            </Button>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy || saving}
              onClick={() => {
                setMoveError(null);
                setMoving({ taskId: selectedEntry.taskId, title: selectedEntry.title, date: selectedEntry.date, time: selectedEntry.time });
              }}
            >
              Move to date…
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setSelected(null)}>
              Close
            </Button>
          </span>
        </div>
      )}
      {moving && (
        <MoveDialog
          heading={selected ? "Move to date" : "Plan task"}
          title={moving.title}
          initialDate={moving.date}
          initialTime={moving.time}
          error={moveError}
          busy={busy || saving}
          onCancel={() => {
            setMoving(null);
            setMoveError(null);
          }}
          onSave={(date, time) =>
            void plan(moving.taskId, date, time).then((ok) => {
              if (ok) {
                setMoving(null);
                setSelected(null);
                setMoveError(null);
              } else setMoveError("The task could not be moved. See the message above.");
            })
          }
        />
      )}

      <div className="relative">
        <div
          ref={regionRef}
          tabIndex={0}
          role="group"
          aria-label="Calendar grid. Use the arrow keys to move between days and Enter to open a day."
          onKeyDown={onGridKeys}
          className="chaos-calendar min-w-0 rounded-xl outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
        >
          <FullCalendar
            ref={calendarRef}
            plugins={[dayGridPlugin, timeGridPlugin, listPlugin, interactionPlugin]}
            initialView={viewTypes[mode]}
            headerToolbar={false}
            height="auto"
            nowIndicator
            navLinks={false}
            eventInteractive
            views={{ agenda: { type: "list", duration: { days: 14 }, buttonText: "Agenda" } }}
            events={events}
            editable
            eventStartEditable
            eventDurationEditable={false}
            droppable
            dayMaxEvents
            slotMinTime="06:00:00"
            slotMaxTime="22:00:00"
            scrollTime="08:00:00"
            allDayText="All day"
            datesSet={datesSet}
            eventClick={eventClick}
            eventDrop={(info) => void eventDrop(info)}
            drop={(info) => void dropFromPanel(info)}
            dayHeaderClassNames={(arg) => cursorClass(arg.date)}
            dayCellClassNames={(arg) => cursorClass(arg.date)}
            dayHeaderContent={(arg: DayHeaderContentArg) =>
              arg.view.type === "dayGridMonth" ? (
                arg.text
              ) : (
                <>
                  {arg.text}
                  <DayBadge day={days?.get(localDate(arg.date))} />
                </>
              )
            }
            dayCellContent={(arg: DayCellContentArg) => (
              <>
                {arg.dayNumberText}
                {arg.view.type === "dayGridMonth" && <DayBadge day={days?.get(localDate(arg.date))} />}
              </>
            )}
            eventContent={(arg: EventContentArg) => {
              const mtg = arg.event.extendedProps.meeting as CalendarMeetingDto | undefined;
              // In the time grid a block is a narrow box: the title leads and wraps, the time sits under it, and the whole text is the hover tooltip.
              if (arg.view.type.startsWith("timeGrid") && !arg.event.allDay) {
                const entry = arg.event.extendedProps.entry as CalendarEntryDto | undefined;
                const title = mtg?.title ?? entry?.title ?? arg.event.title;
                const minutes = arg.event.end && arg.event.start ? (arg.event.end.getTime() - arg.event.start.getTime()) / 60000 : 60;
                const tooltip = arg.timeText ? `${title} (${arg.timeText})` : title;
                const repeat = entry?.kind === "Repeat" && <Repeat className="mt-0.5 size-3 shrink-0" aria-label="Repeats" />;
                // A block of half an hour or less has room for one line: the title, then the time if it fits.
                if (minutes <= 30)
                  return (
                    <span className="flex min-w-0 items-center gap-1 px-1 leading-tight" title={tooltip}>
                      {repeat}
                      <span className="truncate font-medium">{title}</span>
                      {arg.timeText && <span className="shrink-0 text-[0.65rem] opacity-80">{arg.timeText}</span>}
                    </span>
                  );
                return (
                  <span className="flex min-w-0 flex-col px-1 leading-tight" title={tooltip}>
                    <span className="flex min-w-0 items-start gap-1 font-medium">
                      {repeat}
                      <span className="line-clamp-2 break-words">{title}</span>
                    </span>
                    {arg.timeText && <span className="truncate text-[0.65rem] opacity-80">{arg.timeText}</span>}
                  </span>
                );
              }
              if (mtg)
                return (
                  <span className="flex min-w-0 items-center gap-1 px-1">
                    {arg.timeText && <span className="shrink-0 text-[0.7rem] opacity-80">{arg.timeText}</span>}
                    <span className="truncate">{mtg.title}</span>
                  </span>
                );
              const entry = arg.event.extendedProps.entry as CalendarEntryDto;
              return (
                <span className="flex min-w-0 items-center gap-1 px-1">
                  {entry.kind === "Due" && <Flag className="size-3 shrink-0" aria-label="Due" />}
                  {entry.kind === "Repeat" && <Repeat className="size-3 shrink-0" aria-label="Repeats" />}
                  {arg.timeText && entry.kind !== "Due" && <span className="shrink-0 text-[0.7rem] opacity-80">{arg.timeText}</span>}
                  <span className="truncate">{entry.title}</span>
                </span>
              );
            }}
          />
        </div>
        {/* Always mounted so its rows stay registered as drag sources; shown as an overlay so the grid keeps the full width. */}
        <div className={cn("absolute right-0 top-0 z-20 w-80 max-w-full", !showUnscheduled && "hidden")}>
          <UnscheduledPanel
            filters={filters}
            onTotal={setUnscheduledTotal}
            onClose={() => setShowUnscheduled(false)}
            revision={revision + reloads}
            api={api}
            listRef={unscheduledRef}
            onOpen={(id) => void openTask(id)}
            onPlan={(row) => {
              setSelected(null);
              setMoveError(null);
              setMoving({ taskId: row.task.id, title: row.task.title, date: cursorDate ?? today, time: null });
            }}
          />
        </div>
      </div>
      <p className={cn("text-xs text-muted-foreground")}>
        Planned tasks are solid; time blocks with an estimate have a dashed edge; <Flag className="inline size-3" /> marks a due date;{" "}
        <Repeat className="inline size-3" /> marks a repeat and cannot be moved.
      </p>
    </section>
  );
}
