import { TaskViewAction } from "./TaskViewAction";
import React from "react";
import { reminderRpc, waitingRpc, type FollowUpDto, type FollowUpItemResponse, type NotificationItemResponse, type ReminderRuleDto, type ReminderRuleResponse, type ReminderSettingsResponse, type TaskResponse, type WaitingResponse } from "#neoastra";
import { Bell, BellOff, Check, Clock, ExternalLink, MessageSquareText, Plus, Trash2, UserRound, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Count, EmptyState, Field, FieldGroup, InlineError, PageHeader } from "@/components/common";

const operationId = () => crypto.randomUUID();

/** A UTC instant shown in the person's locale. */
export function when(iso: string | null | undefined) {
  return iso ? new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "—";
}

/** A datetime-local input value (local wall time) as a UTC instant. */
export function toUtc(local: string) {
  return new Date(local).toISOString();
}

export function duration(totalMinutes: number) {
  if (totalMinutes % 1440 === 0) return `${totalMinutes / 1440} ${totalMinutes === 1440 ? "day" : "days"}`;
  if (totalMinutes % 60 === 0) return `${totalMinutes / 60} ${totalMinutes === 60 ? "hour" : "hours"}`;
  return `${totalMinutes} min`;
}

/** A rule in plain words: "1 hour before the due time", "At 12 Nov 2026, 09:00". */
export function describeRule(rule: ReminderRuleDto) {
  if (rule.anchor === "Absolute") return `At ${when(rule.absoluteAtUtc)}`;
  const anchor = rule.anchor === "Planned" ? "planned" : "due";
  const offset = rule.offsetMinutes ?? 0;
  return offset === 0 ? `At the ${anchor} time` : `${duration(Math.abs(offset))} ${offset < 0 ? "before" : "after"} the ${anchor} time`;
}

const suspensionText: Record<string, string> = {
  Disabled: "Turned off",
  TaskFinished: "The task is finished",
  NoPlannedDate: "Paused: the task has no planned date",
  NoDueDate: "Paused: the task has no due date",
  NoWaitingStart: "Paused: waiting has not started",
};

const unitMinutes = { minutes: 1, hours: 60, days: 1440 } as const;

/**
 * The task editor's reminder rules (PLAN §11): exact times or offsets from the planned or
 * due date, optionally insistent. Each rule shows when it will next be delivered (after
 * quiet hours and snoozes) or why it is paused.
 */
export function TaskReminders({ task, revision, busy, api = reminderRpc, onChanged }: {
  task: TaskResponse; revision: number; busy: boolean; api?: typeof reminderRpc; onChanged: () => void;
}) {
  const [rules, setRules] = React.useState<ReminderRuleResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);
  const [anchor, setAnchor] = React.useState(task.dueDate ? "Due" : task.plannedDate ? "Planned" : "Absolute");
  const [at, setAt] = React.useState("");
  const [amount, setAmount] = React.useState(1);
  const [unit, setUnit] = React.useState<keyof typeof unitMinutes>("hours");
  const [direction, setDirection] = React.useState<"before" | "after">("before");
  const [insistent, setInsistent] = React.useState(false);
  const [every, setEvery] = React.useState(10);
  const [local, setLocal] = React.useState(0);

  React.useEffect(() => {
    let current = true;
    void api.list({ taskId: task.id }).then(result => {
      if (!current) return;
      if (result.ok) { setRules([...result.rules]); setError(null); } else setError(result.error?.message ?? "Could not load reminders.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, task.id, task.version, revision, local]);

  async function run(action: () => Promise<{ ok: boolean; error: { message: string } | null }>, failure: string) {
    if (working || busy) return;
    setWorking(true); setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? failure);
      setLocal(value => value + 1);
      onChanged();
    } catch (e) { setError(e instanceof Error ? e.message : failure); }
    finally { setWorking(false); }
  }

  function add(event: React.FormEvent) {
    event.preventDefault();
    const minutes = amount * unitMinutes[unit] * (direction === "before" ? -1 : 1);
    const rule: ReminderRuleDto = anchor === "Absolute"
      ? { anchor, offsetMinutes: null, absoluteAtUtc: at ? toUtc(at) : null, insistentMinutes: insistent ? every : null, enabled: true }
      : { anchor, offsetMinutes: minutes, absoluteAtUtc: null, insistentMinutes: insistent ? every : null, enabled: true };
    void run(() => api.add({ operationId: operationId(), taskId: task.id, rule }), "Could not add the reminder.");
  }

  const disabled = busy || working;
  const finished = task.status === "Done" || task.status === "Cancelled";
  return <section className="grid gap-3" aria-labelledby="reminders-heading">
    <h3 id="reminders-heading" className="flex items-center gap-1.5 font-semibold"><Bell className="size-4"/>Reminders{rules.length > 0 && <Count>{rules.length}</Count>}</h3>
    {error && <InlineError>{error}</InlineError>}
    {rules.length === 0 ? <p className="text-sm text-muted-foreground">No reminders yet.</p>
      : <ol className="grid gap-1" aria-label="Reminders">{rules.map(item => <li key={item.id} className="flex min-h-11 items-center gap-2 rounded-lg border bg-card px-3 py-1.5 text-sm">
          <div className="grid min-w-0 flex-1 gap-0.5">
            <span className="font-medium">{describeRule(item.rule)}{item.rule.insistentMinutes !== null && ` · repeats every ${item.rule.insistentMinutes} min until you act`}</span>
            <span className="text-xs text-muted-foreground">{item.nextDeliveryAtUtc ? `Next: ${when(item.nextDeliveryAtUtc)}${item.targetAtUtc && item.nextDeliveryAtUtc !== item.targetAtUtc ? ` (due ${when(item.targetAtUtc)}; moved by quiet hours or a snooze)` : ""}`
              : suspensionText[item.suspension] ?? (item.lastDeliveredAtUtc ? `Delivered ${when(item.lastDeliveredAtUtc)}` : "Nothing scheduled")}</span>
          </div>
          <Button type="button" variant="ghost" size="icon-sm" disabled={disabled || finished} aria-label={item.rule.enabled ? `Turn off reminder: ${describeRule(item.rule)}` : `Turn on reminder: ${describeRule(item.rule)}`}
            title={item.rule.enabled ? "Turn off" : "Turn on"}
            onClick={() => void run(() => api.update({ operationId: operationId(), ruleId: item.id, expectedVersion: item.version, rule: { ...item.rule, enabled: !item.rule.enabled } }), "Could not change the reminder.")}>
            {item.rule.enabled ? <Bell/> : <BellOff/>}
          </Button>
          <Button type="button" variant="ghost" size="icon-sm" disabled={disabled} aria-label={`Delete reminder: ${describeRule(item.rule)}`} title="Delete"
            onClick={() => void run(() => api.delete({ operationId: operationId(), ruleId: item.id, expectedVersion: item.version }), "Could not delete the reminder.")}><Trash2/></Button>
        </li>)}</ol>}
    {!finished && <form className="grid gap-3 rounded-lg border border-dashed p-3" aria-label="Add a reminder" onSubmit={add}>
      <Field label="Remind me"><NativeSelect value={anchor} onChange={e => setAnchor(e.target.value)}>
        <NativeSelectOption value="Absolute">At an exact time</NativeSelectOption>
        <NativeSelectOption value="Planned" disabled={!task.plannedDate}>Relative to the planned date</NativeSelectOption>
        <NativeSelectOption value="Due" disabled={!task.dueDate}>Relative to the due date</NativeSelectOption>
      </NativeSelect></Field>
      {anchor === "Absolute"
        ? <Field label="When"><Input type="datetime-local" required value={at} onChange={e => setAt(e.target.value)}/></Field>
        : <div className="grid grid-cols-3 gap-2">
            <Field label="Amount"><Input type="number" min="0" max="999" required value={amount} onChange={e => setAmount(Number(e.target.value))}/></Field>
            <Field label="Unit"><NativeSelect value={unit} onChange={e => setUnit(e.target.value as keyof typeof unitMinutes)}>
              <NativeSelectOption value="minutes">minutes</NativeSelectOption><NativeSelectOption value="hours">hours</NativeSelectOption><NativeSelectOption value="days">days</NativeSelectOption>
            </NativeSelect></Field>
            <Field label="Direction"><NativeSelect value={direction} onChange={e => setDirection(e.target.value as "before" | "after")}>
              <NativeSelectOption value="before">before</NativeSelectOption><NativeSelectOption value="after">after</NativeSelectOption>
            </NativeSelect></Field>
          </div>}
      <p className="text-xs text-muted-foreground">A date without a time uses your default reminder time.</p>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex h-9 items-center gap-2 text-sm"><input type="checkbox" checked={insistent} onChange={e => setInsistent(e.target.checked)}/>Repeat until I act</label>
        {insistent && <Field label="Every (minutes, at least 5)" className="w-48"><Input type="number" min="5" max="1440" value={every} onChange={e => setEvery(Number(e.target.value))}/></Field>}
      </div>
      <div><Button variant="outline" disabled={disabled}><Plus/>Add reminder</Button></div>
    </form>}
  </section>;
}

type FollowUpKind = "None" | "Fixed" | "AfterWaiting" | "BeforeDue" | "AfterInactivity";

function describeFollowUp(followUp: FollowUpDto) {
  const unit = (followUp.unit ?? "Days").toLowerCase();
  switch (followUp.kind) {
    case "Fixed": return `At ${when(followUp.fixedAtUtc)}`;
    case "AfterWaiting": return `${followUp.amount} ${unit} after waiting started`;
    case "BeforeDue": return `${followUp.amount} ${unit} before the due date`;
    default: return `After ${followUp.amount} ${followUp.amount === 1 ? "day" : "days"} without activity`;
  }
}

/**
 * Waiting on someone outside the app (PLAN §11). Only the name is stored; nothing is ever
 * sent to them. A follow-up appears in My Day and as a notification; logging it writes a
 * private diary update.
 */
export function TaskWaiting({ task, revision, busy, api = waitingRpc, onChanged }: {
  task: TaskResponse; revision: number; busy: boolean; api?: typeof waitingRpc; onChanged: (task: TaskResponse | null) => void;
}) {
  const [view, setView] = React.useState<WaitingResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);
  const [person, setPerson] = React.useState("");
  const [kind, setKind] = React.useState<FollowUpKind>("None");
  const [at, setAt] = React.useState("");
  const [amount, setAmount] = React.useState(3);
  const [unit, setUnit] = React.useState("Days");
  const [note, setNote] = React.useState("");

  React.useEffect(() => {
    let current = true;
    void api.get({ taskId: task.id }).then(result => {
      if (!current) return;
      if (!result.ok) { setError(result.error?.message ?? "Could not load waiting details."); return; }
      setView(result); setError(null);
      setPerson(result.person ?? "");
      setKind((result.followUp?.kind as FollowUpKind | undefined) ?? "None");
      if (result.followUp?.amount) setAmount(result.followUp.amount);
      if (result.followUp?.unit) setUnit(result.followUp.unit);
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, task.id, task.version, revision]);

  async function run(action: () => Promise<WaitingResponse>, failure: string, after?: () => void) {
    if (working || busy) return;
    setWorking(true); setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? failure);
      setView(result);
      after?.();
      onChanged(result.task);
    } catch (e) { setError(e instanceof Error ? e.message : failure); }
    finally { setWorking(false); }
  }

  function save(event: React.FormEvent) {
    event.preventDefault();
    const followUp: FollowUpDto | null = kind === "None" ? null
      : kind === "Fixed" ? { kind, fixedAtUtc: at ? toUtc(at) : null, amount: null, unit: null }
      : { kind, fixedAtUtc: null, amount, unit: kind === "AfterInactivity" ? "Days" : unit };
    void run(() => api.set({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, person, followUp }), "Could not save the waiting details.");
  }

  const disabled = busy || working;
  const finished = task.status === "Done" || task.status === "Cancelled";
  const waiting = view?.person ? view : null;
  return <section className="grid gap-3" aria-labelledby="waiting-heading">
    <h3 id="waiting-heading" className="flex items-center gap-1.5 font-semibold"><UserRound className="size-4"/>Waiting on someone</h3>
    <p className="text-xs text-muted-foreground">Kept privately with this task. Chaos never contacts this person.</p>
    {error && <InlineError>{error}</InlineError>}
    {waiting && <div className="grid gap-1 rounded-lg border bg-muted/40 px-3 py-2 text-sm" role="status">
      <span>Waiting on <bdi className="font-medium">{waiting.person}</bdi> since {when(waiting.startedAtUtc)}</span>
      {waiting.followUp && <span className="text-muted-foreground">Follow up: {describeFollowUp(waiting.followUp)} · {waiting.followUpDueAtUtc ? `due ${when(waiting.followUpDueAtUtc)}` : suspensionText[waiting.followUpSuspension] ?? "paused"}</span>}
      {waiting.lastActivityAtUtc && <span className="text-xs text-muted-foreground">Last activity {when(waiting.lastActivityAtUtc)}</span>}
    </div>}
    {!finished && <form className="grid gap-3" aria-label="Waiting details" onSubmit={save}>
      <Field label="Person"><Input dir="auto" required maxLength={200} value={person} onChange={e => setPerson(e.target.value)} placeholder="Who are you waiting on?"/></Field>
      <FieldGroup legend="Follow up">
        <NativeSelect aria-label="Follow-up rule" value={kind} onChange={e => setKind(e.target.value as FollowUpKind)}>
          <NativeSelectOption value="None">No follow-up</NativeSelectOption>
          <NativeSelectOption value="Fixed">At a date and time</NativeSelectOption>
          <NativeSelectOption value="AfterWaiting">Some time after waiting started</NativeSelectOption>
          <NativeSelectOption value="BeforeDue" disabled={!task.dueDate}>Some time before the due date</NativeSelectOption>
          <NativeSelectOption value="AfterInactivity">After days without activity</NativeSelectOption>
        </NativeSelect>
        {kind === "Fixed" && <Field label="When"><Input type="datetime-local" required value={at} onChange={e => setAt(e.target.value)}/></Field>}
        {(kind === "AfterWaiting" || kind === "BeforeDue" || kind === "AfterInactivity") && <div className="grid grid-cols-2 gap-2">
          <Field label={kind === "AfterInactivity" ? "Days" : "Amount"}><Input type="number" min="1" max="999" required value={amount} onChange={e => setAmount(Number(e.target.value))}/></Field>
          {kind !== "AfterInactivity" && <Field label="Unit"><NativeSelect value={unit} onChange={e => setUnit(e.target.value)}>
            {["Minutes", "Hours", "Days", "Weeks"].map(value => <NativeSelectOption key={value} value={value}>{value.toLowerCase()}</NativeSelectOption>)}
          </NativeSelect></Field>}
        </div>}
      </FieldGroup>
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" disabled={disabled || !person.trim()}>{waiting ? "Save waiting details" : "Start waiting"}</Button>
        {waiting && <Button type="button" variant="ghost" disabled={disabled}
          onClick={() => void run(() => api.clear({ operationId: operationId(), taskId: task.id, expectedVersion: task.version, nextStatus: null }), "Could not stop waiting.", () => { setPerson(""); setKind("None"); })}>
          <Check/>Stop waiting</Button>}
      </div>
    </form>}
    {waiting && !finished && <form className="flex gap-2" aria-label="Log a follow-up" onSubmit={event => { event.preventDefault(); void run(() => api.log({ operationId: operationId(), taskId: task.id, note }), "Could not log the follow-up.", () => setNote("")); }}>
      <label htmlFor="follow-up-note" className="sr-only">Follow-up note</label>
      <Input id="follow-up-note" dir="auto" value={note} onChange={e => setNote(e.target.value)} placeholder="What happened? (optional)"/>
      <Button variant="outline" disabled={disabled}><MessageSquareText/>Log follow-up</Button>
    </form>}
  </section>;
}

/**
 * My Day's follow-up items (PLAN §11): separate from the chosen tasks, each refers to its
 * original task. Open, Log, Snooze and Stop waiting; nothing is sent to anyone.
 */
export function FollowUps({ day, revision, busy, api = waitingRpc, onOpen, onChanged }: {
  day: string; revision: number; busy: boolean; api?: typeof waitingRpc; onOpen: (task: TaskResponse) => void; onChanged: () => void;
}) {
  const [items, setItems] = React.useState<FollowUpItemResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);
  const [logging, setLogging] = React.useState<string | null>(null);
  const [note, setNote] = React.useState("");
  const [local, setLocal] = React.useState(0);

  React.useEffect(() => {
    let current = true;
    void api.due({ date: day }).then(result => {
      if (!current) return;
      if (result.ok) { setItems([...result.items]); setError(null); } else setError(result.error?.message ?? "Could not load follow-ups.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, day, revision, local]);

  async function run(action: () => Promise<WaitingResponse>, failure: string) {
    if (working || busy) return;
    setWorking(true); setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? failure);
      setLogging(null); setNote("");
      setLocal(value => value + 1);
      onChanged();
    } catch (e) { setError(e instanceof Error ? e.message : failure); }
    finally { setWorking(false); }
  }

  if (items.length === 0 && !error) return null;
  const disabled = busy || working;
  return <section aria-labelledby="follow-ups-heading" className="grid gap-1.5">
    <h3 id="follow-ups-heading" className="flex items-center gap-1.5 border-b px-1 pt-3 pb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase"><UserRound className="size-3.5"/>Follow-ups<Count>{items.length}</Count></h3>
    {error && <InlineError>{error}</InlineError>}
    <ol className="grid gap-1" aria-label="Follow-ups">{items.map(item => <li key={item.task.id} className="grid gap-2 rounded-lg border bg-card px-3 py-2 shadow-xs">
      <div className="flex flex-wrap items-center gap-2">
        <button type="button" className="rounded-sm px-1 text-left font-medium [overflow-wrap:anywhere] hover:text-primary" disabled={disabled} onClick={() => onOpen(item.task)}>{item.task.title}</button>
        <span className="text-xs text-muted-foreground">Follow up{item.person ? <> with <bdi>{item.person}</bdi></> : ""} · {when(item.dueAtUtc)}</span>
        {item.overdue && <Badge variant="outline">Due</Badge>}
        <TaskViewAction task={item.task} title={item.task.title} disabled={disabled}/>
      </div>
      <div className="flex flex-wrap gap-1">
        <Button type="button" size="xs" variant="outline" disabled={disabled} onClick={() => onOpen(item.task)}><ExternalLink/>Open</Button>
        <Button type="button" size="xs" variant="outline" disabled={disabled} aria-expanded={logging === item.task.id} onClick={() => setLogging(current => current === item.task.id ? null : item.task.id)}><MessageSquareText/>Log</Button>
        <Button type="button" size="xs" variant="outline" disabled={disabled} onClick={() => void run(() => api.snooze({ operationId: operationId(), taskId: item.task.id, choice: "OneHour", untilUtc: null }), "Could not snooze the follow-up.")}><Clock/>1 hour</Button>
        <Button type="button" size="xs" variant="outline" disabled={disabled} onClick={() => void run(() => api.snooze({ operationId: operationId(), taskId: item.task.id, choice: "Tomorrow", untilUtc: null }), "Could not snooze the follow-up.")}><Clock/>Tomorrow</Button>
        <Button type="button" size="xs" variant="ghost" disabled={disabled} onClick={() => void run(() => api.clear({ operationId: operationId(), taskId: item.task.id, expectedVersion: item.task.version, nextStatus: null }), "Could not stop waiting.")}><Check/>Stop waiting</Button>
      </div>
      {logging === item.task.id && <form className="flex gap-2" onSubmit={event => { event.preventDefault(); void run(() => api.log({ operationId: operationId(), taskId: item.task.id, note }), "Could not log the follow-up."); }}>
        <label htmlFor={`log-${item.task.id}`} className="sr-only">Follow-up note for {item.task.title}</label>
        <Input id={`log-${item.task.id}`} dir="auto" autoFocus value={note} onChange={e => setNote(e.target.value)} placeholder="What happened? (optional)"/>
        <Button disabled={disabled}>Save</Button>
      </form>}
    </li>)}</ol>
  </section>;
}

/**
 * Due reminders and follow-ups as in-app alerts with Open, Done, Snooze and Dismiss (PLAN
 * §11). Asking for alerts also delivers anything that has become due. Every action carries
 * its own operation ID and the notification's generation, so a repeated click or an alert
 * from a replaced schedule is never applied twice.
 */
export function AlertStack({ api = reminderRpc, intervalMs = 15_000, onOpen, onChanged }: {
  api?: typeof reminderRpc; intervalMs?: number; onOpen: (taskId: string) => void; onChanged: () => void;
}) {
  const [alerts, setAlerts] = React.useState<NotificationItemResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState<string | null>(null);
  const [needsChoice, setNeedsChoice] = React.useState<string | null>(null);

  const load = React.useCallback(async () => {
    try {
      const result = await api.alerts({ limit: null });
      if (result.ok) setAlerts([...result.items]);
    } catch { /* the next poll retries; capture never waits on reminders */ }
  }, [api]);

  React.useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), intervalMs);
    function wake() { if (document.visibilityState === "visible") void load(); }
    window.addEventListener("focus", wake);
    document.addEventListener("visibilitychange", wake);
    return () => { clearInterval(timer); window.removeEventListener("focus", wake); document.removeEventListener("visibilitychange", wake); };
  }, [load, intervalMs]);

  async function act(item: NotificationItemResponse, action: string, snooze: string | null = null, openSubtaskChoice: string | null = null) {
    if (working) return;
    setWorking(item.jobId); setError(null);
    try {
      const result = await api.act({ operationId: operationId(), jobId: item.jobId, generation: item.generation, action, snooze, snoozeUntilUtc: null, openSubtaskChoice });
      if (!result.ok) {
        if (result.error?.fields.some(field => field.fieldPath === "openSubtaskChoice")) { setNeedsChoice(item.jobId); return; }
        throw new Error(result.error?.message ?? "Could not act on the reminder.");
      }
      setNeedsChoice(null);
      if (result.outcome === "Stale") setError("That reminder was replaced by a newer one.");
      if (action === "Open" && result.taskId) onOpen(result.taskId);
      if (action === "Done" || result.outcome === "Completed") onChanged();
      await load();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not act on the reminder."); }
    finally { setWorking(null); }
  }

  if (alerts.length === 0 && !error) return null;
  return <section aria-label="Reminders" className="fixed right-4 bottom-4 z-30 grid w-[min(24rem,calc(100vw-2rem))] gap-2">
    {error && <div role="alert" className="flex items-center gap-2 rounded-xl border bg-popover px-3 py-2 text-sm shadow-lg">{error}<Button type="button" variant="ghost" size="icon-sm" className="ml-auto" aria-label="Dismiss message" onClick={() => setError(null)}><X/></Button></div>}
    {alerts.slice(0, 3).map(item => <article key={item.jobId} aria-label={`Reminder: ${item.taskTitle}`} className="grid gap-2 rounded-xl border bg-popover p-3 text-sm shadow-lg">
      <div className="flex items-start gap-2">
        <Bell className="mt-0.5 size-4 shrink-0 text-primary"/>
        <div className="grid min-w-0 flex-1 gap-0.5">
          <span className="font-medium [overflow-wrap:anywhere]">{item.taskTitle}</span>
          <span className="text-xs text-muted-foreground">{item.source === "FollowUp" ? <>Follow up{item.person ? <> with <bdi>{item.person}</bdi></> : ""}</> : "Reminder"} · {when(item.targetAtUtc)}{item.repeat ? " · repeating" : ""}</span>
          {item.late && <span className="text-xs text-muted-foreground">Missed while Chaos was closed or asleep.</span>}
        </div>
      </div>
      {needsChoice === item.jobId
        ? <div className="flex flex-wrap items-center gap-1" role="group" aria-label="Open subtasks">
            <span className="text-xs">This task has open subtasks.</span>
            <Button type="button" size="xs" disabled={working !== null} onClick={() => void act(item, "Done", null, "CompleteAll")}>Complete them too</Button>
            <Button type="button" size="xs" variant="outline" disabled={working !== null} onClick={() => void act(item, "Done", null, "KeepOpen")}>Keep them open</Button>
          </div>
        : <div className="flex flex-wrap gap-1">
            <Button type="button" size="xs" variant="outline" disabled={working !== null} onClick={() => void act(item, "Open")}><ExternalLink/>Open</Button>
            <Button type="button" size="xs" disabled={working !== null} onClick={() => void act(item, "Done")}><Check/>Done</Button>
            <Button type="button" size="xs" variant="outline" disabled={working !== null} onClick={() => void act(item, "Snooze", "TenMinutes")}>10 min</Button>
            <Button type="button" size="xs" variant="outline" disabled={working !== null} onClick={() => void act(item, "Snooze", "OneHour")}>1 hour</Button>
            <Button type="button" size="xs" variant="outline" disabled={working !== null} onClick={() => void act(item, "Snooze", "Tomorrow")}>Tomorrow</Button>
            <Button type="button" size="xs" variant="ghost" disabled={working !== null} aria-label={`Dismiss reminder for ${item.taskTitle}`} onClick={() => void act(item, "Dismiss")}><X/></Button>
          </div>}
    </article>)}
    {alerts.length > 3 && <p className="rounded-xl border bg-popover px-3 py-1.5 text-xs shadow-lg">{alerts.length - 3} more in Notifications</p>}
  </section>;
}

const stateText: Record<string, string> = { Delivered: "Waiting for you", Acknowledged: "Handled", Snoozed: "Snoozed" };
const presentationText: Record<string, string> = { System: "Shown by Windows", InApp: "Shown in Chaos", Suppressed: "Not shown (blocked or quiet)", NotYet: "Not shown yet" };

/**
 * Notification settings and the notification center (PLAN §11): default reminder time,
 * quiet hours (may cross midnight), what this build can deliver, and a durable history of
 * due reminders including missed and suppressed ones.
 */
export function NotificationCenter({ revision, api = reminderRpc, onOpen }: { revision: number; api?: typeof reminderRpc; onOpen: (taskId: string) => void }) {
  const [settings, setSettings] = React.useState<ReminderSettingsResponse | null>(null);
  const [draft, setDraft] = React.useState({ defaultReminderTime: "09:00", quietHoursEnabled: false, quietStart: "22:00", quietEnd: "07:00" });
  const [items, setItems] = React.useState<NotificationItemResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [saved, setSaved] = React.useState(false);
  const [working, setWorking] = React.useState(false);

  const adopt = React.useCallback((result: ReminderSettingsResponse) => {
    setSettings(result);
    setDraft({ defaultReminderTime: result.defaultReminderTime ?? "09:00", quietHoursEnabled: result.quietHoursEnabled, quietStart: result.quietStart ?? "22:00", quietEnd: result.quietEnd ?? "07:00" });
  }, []);

  React.useEffect(() => {
    let current = true;
    void Promise.all([api.settings({}), api.center({ limit: 100 })]).then(([loaded, center]) => {
      if (!current) return;
      if (loaded.ok) adopt(loaded); else setError(loaded.error?.message ?? "Could not load notification settings.");
      if (center.ok) setItems([...center.items]);
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, revision, adopt]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!settings?.version) return;
    setWorking(true); setError(null); setSaved(false);
    try {
      const result = await api.updateSettings({ operationId: operationId(), expectedVersion: settings.version, ...draft });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not save the settings.");
      adopt(result); setSaved(true);
    } catch (e) { setError(e instanceof Error ? e.message : "Could not save the settings."); }
    finally { setWorking(false); }
  }

  const capabilities = settings?.capabilities;
  return <>
    <PageHeader eyebrow="Reminders" title="Notifications" subtitle="Reminders and follow-ups stay on this device. Nothing is sent to anyone."/>
    {error && <InlineError>{error}</InlineError>}
    {capabilities && <p className="rounded-xl border bg-muted/40 p-3 text-sm" role="note"><span className="font-medium">{capabilities.adapterName}.</span> {capabilities.description}</p>}
    <form className="grid max-w-xl gap-3 rounded-xl border p-4" aria-label="Notification settings" onSubmit={event => void save(event)}>
      <Field label="Default reminder time (for dates without a time)"><Input type="time" required value={draft.defaultReminderTime} onChange={e => setDraft({ ...draft, defaultReminderTime: e.target.value })}/></Field>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={draft.quietHoursEnabled} onChange={e => setDraft({ ...draft, quietHoursEnabled: e.target.checked })}/>Quiet hours: hold notifications until they end</label>
      {draft.quietHoursEnabled && <div className="grid grid-cols-2 gap-3">
        <Field label="From"><Input type="time" required value={draft.quietStart} onChange={e => setDraft({ ...draft, quietStart: e.target.value })}/></Field>
        <Field label="Until"><Input type="time" required value={draft.quietEnd} onChange={e => setDraft({ ...draft, quietEnd: e.target.value })}/></Field>
      </div>}
      <div className="flex items-center gap-3"><Button disabled={working || !settings}>Save settings</Button>{saved && <span className="text-sm text-muted-foreground" role="status">Saved. Pending reminders were rescheduled.</span>}</div>
    </form>
    <section aria-labelledby="center-heading" className="grid gap-1.5">
      <h3 id="center-heading" className="flex items-center gap-1.5 font-semibold"><Bell className="size-4"/>History<Count>{items.length}</Count></h3>
      {items.length === 0 ? <EmptyState title="No notifications yet.">Due reminders and follow-ups are listed here, including any missed while Chaos was closed.</EmptyState>
        : <ol className="grid gap-1" aria-label="Notification history">{items.map(item => <li key={item.jobId} className="flex min-h-11 flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border bg-card px-3 py-1.5 text-sm">
            <button type="button" className="rounded-sm px-1 text-left font-medium [overflow-wrap:anywhere] hover:text-primary" onClick={() => onOpen(item.taskId)}>{item.taskTitle}</button>
            <span className="text-xs text-muted-foreground">{item.source === "FollowUp" ? "Follow-up" : "Reminder"} for {when(item.targetAtUtc)} · delivered {when(item.deliveredAtUtc)}</span>
            <Badge variant="outline">{stateText[item.state] ?? item.state}{item.actionTaken && item.state === "Acknowledged" ? `: ${item.actionTaken}` : ""}</Badge>
            <span className="text-xs text-muted-foreground">{presentationText[item.presentation] ?? item.presentation}</span>
            {item.late && <Badge variant="outline">Missed</Badge>}
          </li>)}</ol>}
    </section>
  </>;
}
