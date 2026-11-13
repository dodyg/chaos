import { TaskViewAction } from "./TaskViewAction";
import { asCategory, categoryIcon, categoryTone, type Focus } from "./category";
import React from "react";
import { DateText } from "./dates";
import { GitHubTaskMark } from "./GitHubTaskMark";
import { myWeekRpc, type ContainerResponse, type DayClockResponse, type DayReconciliationDto, type TaskResponse, type WeekBoardDto, type WeekDayDto, type WeekDraftDto, type WeekDraftGoalDto, type WeekGoalDto, type WeekOverviewDto, type WeekTaskDto } from "#neoastra";
import { AlertTriangle, ArrowLeft, ArrowRight, CalendarDays, Check, ChevronLeft, ChevronRight, Clock, Flag, Lightbulb, Plus, Settings2, Target, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Count, EmptyState, Field, InlineError, PageHeader, PriorityBadge, StatusBadge, isFinished, label } from "@/components/common";
import { createLatestRequest } from "./latestRequest";
import { addDays } from "./MyDay";

const operationId = () => crypto.randomUUID();
const weekDragType = "application/x-chaos-week-task";
export const steps = ["Look back", "Decide leftovers", "Set goals", "Plan days", "Confirm"] as const;
const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const decisions: Record<string, string> = { Carry: "Carry into this week", Later: "Schedule later", Remove: "Drop from my plan", Cancel: "Cancel the task" };
const candidateText: Record<string, string> = { Overdue: "Overdue", DueThisWeek: "Due this week", PlannedThisWeek: "Planned this week", OnMyDayToday: "On My Day" };

/** "Tue 10" for a yyyy-MM-dd local date (calendar arithmetic only; no time zone involved). */
export function dayLabel(date: string) {
  const [year, month, day] = date.split("-").map(Number);
  return `${weekdays[new Date(Date.UTC(year, month - 1, day)).getUTCDay()].slice(0, 3)} ${day}`;
}

export function minutesText(total: number) {
  const hours = Math.floor(total / 60);
  const rest = total % 60;
  return hours > 0 ? (rest > 0 ? `${hours} h ${rest} min` : `${hours} h`) : `${rest} min`;
}

/** Hours typed by a person into whole minutes; blank means "no value". */
function toMinutes(hours: string) {
  if (hours.trim() === "") return null;
  const value = Number(hours.replace(",", "."));
  return Number.isFinite(value) ? Math.round(value * 60) : null;
}
const toHours = (minutes: number | null) => minutes === null ? "" : String(Math.round(minutes / 6) / 10);

/**
 * Moves a task to a day (or the pool when `day` is null) in a draft; the task keeps one place
 * in the week and goes to the end of its new day.
 */
export function placeTask(draft: WeekDraftDto, taskId: string, day: string | null): WeekDraftDto {
  return { ...draft, assignments: [...draft.assignments.filter(item => item.taskId !== taskId), { taskId, day }] };
}

/** Records a leftover decision; carrying a task puts it in the week's pool, other choices take it off the week. */
export function decideLeftover(draft: WeekDraftDto, taskId: string, decision: string | null, laterDate: string | null = null): WeekDraftDto {
  const leftovers = draft.leftovers.filter(item => item.taskId !== taskId);
  let assignments = draft.assignments;
  if (decision === "Carry") {
    if (!assignments.some(item => item.taskId === taskId)) assignments = [...assignments, { taskId, day: null }];
  } else {
    assignments = assignments.filter(item => item.taskId !== taskId);
  }
  return { ...draft, assignments, leftovers: decision ? [...leftovers, { taskId, decision, laterDate: decision === "Later" ? laterDate : null }] : leftovers };
}

/**
 * My Week (PLAN §10): the confirmed week with goals, per-day capacity and the midweek
 * check-in, and the five-step planning ritual. The draft is saved separately from the
 * confirmed plan, so cancelling leaves the confirmed week exactly as it was. Nothing here
 * changes a task's planned or due date, except an explicit "schedule later" date.
 */
export function MyWeek({ clock, revision, busy, containers, api = myWeekRpc, onOpen, onChanged, focus = null }: {
  clock: DayClockResponse | null; revision: number; busy: boolean; containers: ContainerResponse[];
  api?: typeof myWeekRpc; onOpen: (task: TaskResponse) => void; onChanged: () => void;
  /** Work/Personal focus for the confirmed board; planning always shows the whole week. */
  focus?: Focus;
}) {
  const [date, setDate] = React.useState<string | null>(null);
  const [week, setWeek] = React.useState<WeekOverviewDto | null>(null);
  const [draft, setDraft] = React.useState<WeekDraftDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);
  const [tab, setTab] = React.useState("plan");
  const [local, setLocal] = React.useState(0);
  const requests = React.useRef(createLatestRequest());
  const today = clock?.today ?? null;

  React.useEffect(() => {
    if (today === null) return;
    const isCurrent = requests.current.begin();
    void api.open({ date }).then(result => {
      if (!isCurrent()) return;
      if (!result.ok || !result.week) { setError(result.error?.message ?? "Could not open this week."); return; }
      setWeek(result.week); setDraft(result.week.draft); setError(null);
    }).catch((e: unknown) => { if (isCurrent()) setError(String(e)); });
  }, [api, date, today, revision, local]);

  async function run(action: () => Promise<{ ok: boolean; week: WeekOverviewDto | null; error: { message: string } | null }>, failure: string, notify = false) {
    if (working || busy) return false;
    setWorking(true); setError(null);
    try {
      const result = await action();
      if (!result.ok || !result.week) throw new Error(result.error?.message ?? failure);
      setWeek(result.week); setDraft(result.week.draft);
      if (notify) onChanged();
      return true;
    } catch (e) { setError(e instanceof Error ? e.message : failure); return false; }
    finally { setWorking(false); }
  }

  if (today === null || week === null) return <>{error && <InlineError>{error}</InlineError>}<p className="text-sm text-muted-foreground" role="status">Loading My Week…</p></>;
  const tasks = new Map(week.tasks.map(item => [item.task.id, item]));
  const disabled = busy || working;
  const save = (next: WeekDraftDto) => run(() => api.saveDraft({ operationId: operationId(), weekStart: week.weekStart, expectedDraftRevision: week.draftRevision, draft: next }), "Could not save the draft.");
  const thisWeek = week.weekStart <= today && today <= week.weekEnd;

  const header = <PageHeader eyebrow={thisWeek ? "This week" : "My Week"} title={`${dayLabel(week.weekStart)} – ${dayLabel(week.weekEnd)}`}
    subtitle={week.state === "Confirmed" ? `Plan confirmed${week.confirmedRevision !== "1" ? ` (revision ${week.confirmedRevision})` : ""}. Weekly plans never change a task's planned or due date.`
      : week.editable ? "Not planned yet. Planning takes five short steps." : "This week was not planned."}
    actions={<nav aria-label="Week" className="flex items-center gap-1">
      <Button type="button" variant="ghost" size="icon-sm" aria-label="Previous week" disabled={working} onClick={() => setDate(addDays(week.weekStart, -1))}><ChevronLeft/></Button>
      <Button type="button" variant={thisWeek ? "secondary" : "outline"} size="sm" disabled={working || thisWeek} onClick={() => setDate(null)}>This week</Button>
      <Button type="button" variant="ghost" size="icon-sm" aria-label="Next week" disabled={working} onClick={() => setDate(addDays(week.weekEnd, 1))}><ChevronRight/></Button>
    </nav>}/>;

  if (draft && week.editable) {
    return <>
      {header}
      {error && <InlineError>{error}</InlineError>}
      <Ritual week={week} draft={draft} tasks={tasks} today={today} containers={containers} disabled={disabled} onOpen={onOpen}
        onEdit={setDraft} onSave={save}
        onOutcome={(goal, outcome) => void run(() => api.setGoalOutcome({ operationId: operationId(), goalId: goal.id, expectedVersion: goal.version, outcome }), "Could not save the outcome.")}
        onCancel={() => void run(() => api.discardDraft({ operationId: operationId(), weekStart: week.weekStart, expectedDraftRevision: week.draftRevision }), "Could not cancel planning.")}
        onConfirm={async () => { await save(draft); }}
        onConfirmSaved={(revisionToConfirm) => void run(() => api.confirm({ operationId: operationId(), weekStart: week.weekStart, expectedDraftRevision: revisionToConfirm }), "Could not confirm the week.", true)}/>
    </>;
  }

  return <>
    {header}
    {error && <InlineError>{error}</InlineError>}
    <Tabs value={tab} onValueChange={setTab} className="gap-4">
    <TabsList className="w-fit max-w-full"><TabsTrigger value="plan">Plan</TabsTrigger><TabsTrigger value="updates">Updates{week.pendingReconciliation.length > 0 && <span className="text-xs tabular-nums">{week.pendingReconciliation.length}</span>}</TabsTrigger><TabsTrigger value="settings">Settings{!week.availabilityConfigured && week.editable && <span className="text-xs">•</span>}</TabsTrigger></TabsList>
    <TabsContent value="plan" className="grid gap-5">
    {!week.availabilityConfigured && week.editable && <p className="text-sm text-muted-foreground">Set your available hours to see capacity warnings. <button type="button" className="text-primary underline" onClick={() => setTab("settings")}>Set availability</button></p>}
    {week.editable && <div className="flex flex-wrap gap-2">
      <Button type="button" disabled={disabled} onClick={() => void run(() => api.startDraft({ operationId: operationId(), weekStart: week.weekStart }), "Could not start planning.")}>
        <CalendarDays/>{week.state === "Confirmed" ? "Review this week's plan" : "Plan this week"}</Button>
    </div>}
    {week.state === "Confirmed"
      ? <>
          <Goals goals={week.confirmed.goals} disabled={disabled}
            onOutcome={(goal, outcome) => void run(() => api.setGoalOutcome({ operationId: operationId(), goalId: goal.id, expectedVersion: goal.version, outcome }), "Could not save the outcome.")}/>
          <Board board={week.confirmed} tasks={tasks} today={today} onOpen={onOpen} focus={focus}/>
        </>
      : <EmptyState title={week.editable ? "No plan for this week yet." : "No plan was made for this week."}>
          {week.editable ? "Plan the week to choose goals and spread tasks across the days." : "Earlier weeks are kept for review."}
        </EmptyState>}
    </TabsContent>
    <TabsContent value="updates" className="grid gap-5">
      {week.pendingReconciliation.map(item => <Reconciliation key={item.date} item={item} tasks={tasks} disabled={disabled}
        onApply={(add, remove) => void run(() => api.reconcile({ operationId: operationId(), date: item.date, weekPlanId: item.weekPlanId, revision: item.revision, addTaskIds: add, removeTaskIds: remove }), "Could not update the day.", true)}/>)}
      {week.checkIn?.midweek && <CheckIn week={week} tasks={tasks} onOpen={onOpen}/>}
      {week.pendingReconciliation.length === 0 && !week.checkIn?.midweek && <EmptyState title="No updates for this week.">Changes to your daily plans and your midweek check-in will appear here.</EmptyState>}
    </TabsContent>
    <TabsContent value="settings" className="grid gap-5">
      {!week.availabilityConfigured && week.editable && <AvailabilityEditor disabled={disabled} onSave={days => void run(() => api.setAvailability({ operationId: operationId(), days }), "Could not save your availability.")}/>}
      <WeekSettings week={week} api={api} disabled={disabled} onChanged={() => { setLocal(value => value + 1); onChanged(); }}/>
    </TabsContent>
    </Tabs>
  </>;
}

function TaskLine({ item, onOpen, children, badge }: { item: WeekTaskDto | undefined; onOpen: (task: TaskResponse) => void; children?: React.ReactNode; badge?: React.ReactNode }) {
  if (!item) return <li className="rounded-md border border-dashed px-2 py-1 text-xs text-muted-foreground">A task that no longer exists</li>;
  const task = item.task;
  return <li className={cn("flex min-w-0 flex-wrap items-center gap-2 rounded-md border bg-card px-2 py-1 text-sm shadow-xs", isFinished(task.status) && "text-muted-foreground [&_[data-slot=title]]:line-through")}>
    <div className="grid min-w-0 flex-[1_1_8rem] grid-cols-[minmax(0,1fr)] gap-0.5">
      <div className="flex min-w-0 items-center gap-1"><button type="button" data-slot="title" className="w-fit max-w-full min-w-0 rounded-sm text-left font-medium [overflow-wrap:anywhere] hover:text-primary focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none" onClick={() => onOpen(task)}>{task.title}</button><GitHubTaskMark taskId={task.id} version={task.version} revision={0}/></div>
      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-muted-foreground [&_[data-slot=badge]]:max-w-full [&_[data-slot=badge]]:whitespace-normal [&_[data-slot=badge]]:[overflow-wrap:anywhere]">
        {badge}
        {task.estimateMinutes !== null ? <span className="inline-flex items-center gap-1"><Clock className="size-3"/>{minutesText(task.estimateMinutes)}</span> : <span>No estimate</span>}
        {task.category === "Work" && <Badge variant="outline">Work</Badge>}
        {task.dueDate && <DateText kind="due" date={task.dueDate}/>}
        <StatusBadge status={task.status}/>
        <PriorityBadge priority={task.priority}/>
      </div>
    </div>
    <TaskViewAction task={task} title={task.title}/>
    {children}
  </li>;
}

function DayHeading({ day, focus = null }: { day: WeekDayDto; focus?: Focus }) {
  const warn = focus === "Personal" ? day.overTotal : focus === "Work" ? day.overWork : day.overTotal || day.overWork;
  if (focus === "Work") return <div className="grid gap-0.5 text-xs">
    <span className="text-sm font-semibold">{dayLabel(day.date)}</span>
    <span className={cn("tabular-nums text-muted-foreground", day.overWork && "font-medium text-destructive")}>Work {minutesText(day.workMinutes)}{day.workAllowanceMinutes !== null ? ` of ${minutesText(day.workAllowanceMinutes)}` : ""}</span>
    {warn && <span className="inline-flex items-center gap-1 text-destructive" role="status"><AlertTriangle className="size-3"/>More work than planned</span>}
  </div>;
  return <div className="grid gap-0.5 text-xs">
    <span className="text-sm font-semibold">{dayLabel(day.date)}</span>
    {focus === "Personal" && <span className="tabular-nums text-muted-foreground">Personal {minutesText(Math.max(0, day.plannedMinutes - day.workMinutes))}</span>}
    <span className={cn("tabular-nums text-muted-foreground", warn && "font-medium text-destructive")}>
      {minutesText(day.plannedMinutes)} of {minutesText(day.availableMinutes)}{day.unknownEstimates > 0 && ` · ${day.unknownEstimates} unestimated`}
    </span>
    {focus === null && day.workAllowanceMinutes !== null && <span className={cn("tabular-nums text-muted-foreground", day.overWork && "font-medium text-destructive")}>Work {minutesText(day.workMinutes)} of {minutesText(day.workAllowanceMinutes)}</span>}
    {warn && <span className="inline-flex items-center gap-1 text-destructive" role="status"><AlertTriangle className="size-3"/>{day.overTotal ? "More than this day holds" : "More work than planned"}</span>}
  </div>;
}

/** The confirmed week read-only: seven day columns and the unassigned pool. */
function Board({ board, tasks, today, onOpen, focus = null }: { board: WeekBoardDto; tasks: Map<string, WeekTaskDto>; today: string; onOpen: (task: TaskResponse) => void; focus?: Focus }) {
  // A placement whose task is gone stays visible so the plan never hides a broken link.
  const inFocus = (taskId: string) => { const item = tasks.get(taskId); return !focus || !item || (asCategory(item.task.category) ?? "Personal") === focus; };
  const placements = board.placements.filter(item => inFocus(item.taskId));
  const hidden = board.placements.length - placements.length;
  const pool = placements.filter(item => item.day === null);
  return <section aria-label="Week plan" className="@container grid min-w-0 gap-3">
    {focus && <p className={cn("inline-flex flex-wrap items-center gap-1 text-sm [&_svg]:size-3.5", categoryTone[focus].text)} role="status">{categoryIcon[focus]}{focus} only{hidden > 0 && <span className="text-muted-foreground">· {hidden} other {hidden === 1 ? "task" : "tasks"} hidden by focus</span>}</p>}
    <div className="grid min-w-0 grid-cols-1 gap-2 @min-[36rem]:grid-cols-2 @min-[54rem]:grid-cols-3">
      {board.days.map(day => <div key={day.date} className={cn("grid min-w-0 grid-cols-[minmax(0,1fr)] content-start gap-1.5 rounded-xl border p-2", day.date === today && "border-primary", day.date < today && "opacity-75")}>
        <DayHeading day={day} focus={focus}/>
        <ol className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-1" aria-label={`Tasks for ${dayLabel(day.date)}`}>
          {placements.filter(item => item.day === day.date).map(item => <TaskLine key={item.taskId} item={tasks.get(item.taskId)} onOpen={onOpen}/>)}
        </ol>
      </div>)}
    </div>
    {pool.length > 0 && <div className="grid gap-1">
      <h3 className="text-sm font-semibold">This week, no day yet<Count>{pool.length}</Count></h3>
      <ol className="grid gap-1">{pool.map(item => <TaskLine key={item.taskId} item={tasks.get(item.taskId)} onOpen={onOpen}/>)}</ol>
    </div>}
  </section>;
}

function GoalProgress({ goal }: { goal: WeekGoalDto }) {
  return goal.countedCount === 0
    ? <span className="text-xs text-muted-foreground">No linked tasks; you judge this one</span>
    : <span className="text-xs text-muted-foreground tabular-nums">{goal.doneCount} of {goal.countedCount} linked tasks done</span>;
}

function OutcomeSelect({ goal, disabled, onOutcome }: { goal: WeekGoalDto; disabled: boolean; onOutcome: (goal: WeekGoalDto, outcome: string) => void }) {
  return <NativeSelect size="sm" aria-label={`Outcome of ${goal.title}`} value={goal.outcome} disabled={disabled} onChange={event => onOutcome(goal, event.target.value)}>
    <NativeSelectOption value="Pending">Pending</NativeSelectOption>
    <NativeSelectOption value="Achieved">Achieved</NativeSelectOption>
    <NativeSelectOption value="LetGo">Let go</NativeSelectOption>
  </NativeSelect>;
}

function Goals({ goals, disabled, onOutcome }: { goals: readonly WeekGoalDto[]; disabled: boolean; onOutcome: (goal: WeekGoalDto, outcome: string) => void }) {
  if (goals.length === 0) return null;
  return <section aria-labelledby="week-goals" className="grid gap-1.5">
    <h3 id="week-goals" className="flex items-center gap-1.5 font-semibold"><Target className="size-4 text-primary"/>Goals<Count>{goals.length}</Count></h3>
    <ul className="grid gap-1">{goals.map(goal => <li key={goal.id} className="flex flex-wrap items-center gap-2 rounded-lg border bg-card px-3 py-2">
      <div className="grid min-w-0 flex-1 gap-0.5">
        <span className="font-medium [overflow-wrap:anywhere]" dir="auto">{goal.title}</span>
        <span className="flex flex-wrap gap-2">{goal.projectName && <Badge variant="outline">{goal.projectName}</Badge>}<GoalProgress goal={goal}/></span>
      </div>
      <OutcomeSelect goal={goal} disabled={disabled} onOutcome={onOutcome}/>
    </li>)}</ul>
  </section>;
}

function CheckIn({ week, tasks, onOpen }: { week: WeekOverviewDto; tasks: Map<string, WeekTaskDto>; onOpen: (task: TaskResponse) => void }) {
  const checkIn = week.checkIn!;
  const quiet = checkIn.slipped.length === 0 && checkIn.overdueTaskIds.length === 0 && checkIn.overloadedDays.length === 0;
  return <section aria-labelledby="check-in" className="grid gap-1.5 rounded-xl border bg-muted/40 p-3">
    <h3 id="check-in" className="flex items-center gap-1.5 font-semibold"><Flag className="size-4 text-primary"/>Midweek check-in</h3>
    {quiet ? <p className="text-sm text-muted-foreground">Everything planned so far is on track.</p> : <>
      <p className="text-xs text-muted-foreground">Nothing is moved for you. Open a task or review the plan to change it.</p>
      {checkIn.slipped.length > 0 && <ul className="grid gap-1" aria-label="Slipped tasks">{checkIn.slipped.map(item =>
        <TaskLine key={item.taskId} item={tasks.get(item.taskId)} onOpen={onOpen} badge={<Badge variant="outline">Planned {dayLabel(item.day)}</Badge>}/>)}</ul>}
      {checkIn.overdueTaskIds.length > 0 && <ul className="grid gap-1" aria-label="Overdue tasks">{checkIn.overdueTaskIds.map(id =>
        <TaskLine key={id} item={tasks.get(id)} onOpen={onOpen} badge={<Badge variant="outline">Overdue</Badge>}/>)}</ul>}
      {checkIn.overloadedDays.length > 0 && <p className="text-sm">Overloaded: {checkIn.overloadedDays.map(dayLabel).join(", ")}</p>}
    </>}
  </section>;
}

/** The explicit add/remove comparison for a day that already had a plan when the week changed. */
function Reconciliation({ item, tasks, disabled, onApply }: { item: DayReconciliationDto; tasks: Map<string, WeekTaskDto>; disabled: boolean; onApply: (add: string[], remove: string[]) => void }) {
  const [add, setAdd] = React.useState<ReadonlySet<string>>(new Set(item.addTaskIds));
  const [remove, setRemove] = React.useState<ReadonlySet<string>>(new Set());
  const toggle = (set: ReadonlySet<string>, id: string) => { const next = new Set(set); if (next.has(id)) next.delete(id); else next.add(id); return next; };
  const title = (id: string) => tasks.get(id)?.task.title ?? "A task";
  return <section aria-label={`Update ${dayLabel(item.date)}`} className="grid gap-2 rounded-xl border border-primary/40 p-3">
    <h3 className="font-semibold">The week plan changed {dayLabel(item.date)}</h3>
    <p className="text-xs text-muted-foreground">Your own choices for that day are kept. Pick what to change.</p>
    {item.addTaskIds.length > 0 && <fieldset className="grid gap-1"><legend className="text-xs font-semibold">Now planned for this day</legend>
      {item.addTaskIds.map(id => <label key={id} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={add.has(id)} onChange={() => setAdd(toggle(add, id))}/>Add {title(id)}</label>)}
    </fieldset>}
    {item.removeTaskIds.length > 0 && <fieldset className="grid gap-1"><legend className="text-xs font-semibold">No longer planned for this day</legend>
      {item.removeTaskIds.map(id => <label key={id} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={remove.has(id)} onChange={() => setRemove(toggle(remove, id))}/>Remove {title(id)}</label>)}
    </fieldset>}
    <div><Button type="button" size="sm" disabled={disabled} onClick={() => onApply([...add], [...remove])}><Check/>Update {dayLabel(item.date)}</Button></div>
  </section>;
}

function AvailabilityEditor({ disabled, onSave }: { disabled: boolean; onSave: (days: { weekday: string; totalMinutes: number; workMinutes: number | null }[]) => void }) {
  const [hours, setHours] = React.useState(() => weekdays.map((_, index) => ({ total: index === 0 || index === 6 ? "0" : "8", work: "" })));
  const [problem, setProblem] = React.useState<string | null>(null);
  function submit(event: React.FormEvent) {
    event.preventDefault();
    const days = [];
    for (let index = 0; index < 7; index++) {
      const total = toMinutes(hours[index].total);
      if (total === null) { setProblem(`Enter the hours available on ${weekdays[index]}.`); return; }
      days.push({ weekday: weekdays[index], totalMinutes: total, workMinutes: toMinutes(hours[index].work) });
    }
    setProblem(null);
    onSave(days);
  }
  return <form onSubmit={submit} aria-labelledby="availability-heading" className="grid gap-2 rounded-xl border p-3">
    <h3 id="availability-heading" className="font-semibold">How much time do you usually have?</h3>
    <p className="text-xs text-muted-foreground">Used to warn when a day holds more than it can. Nothing is ever moved for you. Leave work hours empty if you don't track them separately.</p>
    <div className="grid gap-2 [grid-template-columns:repeat(auto-fill,minmax(8rem,1fr))]">
      {weekdays.map((name, index) => <fieldset key={name} className="grid gap-1 rounded-md border p-2"><legend className="px-1 text-xs font-semibold">{name}</legend>
        <Field label="Hours"><Input inputMode="decimal" value={hours[index].total} onChange={event => setHours(hours.map((item, i) => i === index ? { ...item, total: event.target.value } : item))}/></Field>
        <Field label="Of which work"><Input inputMode="decimal" value={hours[index].work} onChange={event => setHours(hours.map((item, i) => i === index ? { ...item, work: event.target.value } : item))}/></Field>
      </fieldset>)}
    </div>
    {problem && <InlineError>{problem}</InlineError>}
    <div><Button type="submit" size="sm" disabled={disabled}>Save availability</Button></div>
  </form>;
}

function WeekSettings({ week, api, disabled, onChanged }: { week: WeekOverviewDto; api: typeof myWeekRpc; disabled: boolean; onChanged: () => void }) {
  const [choice, setChoice] = React.useState(week.firstWeekday);
  const [preview, setPreview] = React.useState<{ conflicts: readonly { weekStart: string; assignmentCount: number; goalCount: number }[]; newStart: string } | null>(null);
  const [message, setMessage] = React.useState<string | null>(null);
  async function check(next: string) {
    setChoice(next); setPreview(null); setMessage(null);
    if (next === week.firstWeekday) return;
    const result = await api.previewWeekStart({ firstWeekday: next });
    if (!result.ok) { setMessage(result.error?.message ?? "Could not check the change."); return; }
    setPreview({ conflicts: result.conflicts, newStart: result.newCurrentWeekStart ?? "" });
  }
  async function apply() {
    const result = await api.changeWeekStart({ operationId: operationId(), firstWeekday: choice, remap: (preview?.conflicts.length ?? 0) > 0 });
    if (!result.ok) { setMessage(result.error?.message ?? "Could not change the first weekday."); return; }
    setPreview(null);
    setMessage(result.draftWeekStarts.length > 0 ? `Remapped work waits as a draft for the week of ${result.draftWeekStarts.map(dayLabel).join(", ")}. Review and confirm it.` : "Weeks now start on " + choice + ".");
    onChanged();
  }
  return <details className="rounded-xl border p-3">
    <summary className="flex cursor-pointer items-center gap-1.5 text-sm font-medium"><Settings2 className="size-4"/>Week settings</summary>
    <div className="grid gap-2 pt-2">
      <Field label="Weeks start on" className="max-w-56">
        <NativeSelect value={choice} disabled={disabled} onChange={event => void check(event.target.value)}>
          {weekdays.map(name => <NativeSelectOption key={name} value={name}>{name}</NativeSelectOption>)}
        </NativeSelect>
      </Field>
      {preview && <div className="grid gap-1.5 text-sm" role="status">
        {preview.conflicts.length === 0
          ? <p>New weeks will start on {choice}, from the week of {dayLabel(preview.newStart)}. Earlier weeks keep their dates.</p>
          : <><p>These planned weeks cross the new boundary. Their remaining work moves, by date, into drafts for you to confirm again; past days stay as they were:</p>
              <ul className="list-disc pl-5">{preview.conflicts.map(item => <li key={item.weekStart}>Week of {dayLabel(item.weekStart)}: {item.assignmentCount} tasks, {item.goalCount} goals</li>)}</ul></>}
        <div><Button type="button" size="sm" disabled={disabled} onClick={() => void apply()}>{preview.conflicts.length === 0 ? `Start weeks on ${choice}` : "Remap and change"}</Button></div>
      </div>}
      {message && <p className="text-sm" role="status">{message}</p>}
    </div>
  </details>;
}

/** The five-step weekly ritual over the stored draft. */
function Ritual({ week, draft, tasks, today, containers, disabled, onOpen, onEdit, onSave, onOutcome, onCancel, onConfirm, onConfirmSaved }: {
  week: WeekOverviewDto; draft: WeekDraftDto; tasks: Map<string, WeekTaskDto>; today: string; containers: ContainerResponse[]; disabled: boolean;
  onOpen: (task: TaskResponse) => void; onEdit: (draft: WeekDraftDto) => void; onSave: (draft: WeekDraftDto) => Promise<boolean>;
  onOutcome: (goal: WeekGoalDto, outcome: string) => void; onCancel: () => void; onConfirm: () => Promise<void>; onConfirmSaved: (revision: string) => void;
}) {
  const step = Math.min(Math.max(draft.step, 1), 5);
  const go = (next: number) => void onSave({ ...draft, step: next });
  const saved = week.draft !== null && JSON.stringify(week.draft) === JSON.stringify(draft);
  return <section aria-label="Plan the week" className="grid gap-3">
    <ol className="flex flex-wrap gap-1" aria-label="Steps">{steps.map((name, index) =>
      <li key={name}><Button type="button" size="sm" variant={index + 1 === step ? "secondary" : "ghost"} aria-current={index + 1 === step ? "step" : undefined} disabled={disabled} onClick={() => go(index + 1)}>
        {index + 1}. {name}</Button></li>)}</ol>

    {step === 1 && <LookBack week={week} tasks={tasks} disabled={disabled} onOpen={onOpen} onOutcome={onOutcome}/>}
    {step === 2 && <Leftovers week={week} draft={draft} tasks={tasks} today={today} onOpen={onOpen} onEdit={onEdit}/>}
    {step === 3 && <GoalStep week={week} draft={draft} tasks={tasks} containers={containers} onEdit={onEdit}/>}
    {step === 4 && <PlanDays week={week} draft={draft} tasks={tasks} today={today} onOpen={onOpen} onEdit={onEdit}/>}
    {step === 5 && <ConfirmStep week={week} draft={draft} tasks={tasks}/>}

    <div className="flex flex-wrap items-center gap-2 border-t pt-3">
      <Button type="button" variant="ghost" disabled={disabled} onClick={onCancel}><X/>Cancel planning</Button>
      <span className="flex-1"/>
      {!saved && <span className="text-xs text-muted-foreground" role="status">Unsaved changes</span>}
      {step > 1 && <Button type="button" variant="outline" disabled={disabled} onClick={() => go(step - 1)}><ArrowLeft/>Back</Button>}
      {step < 5
        ? <Button type="button" disabled={disabled} onClick={() => go(step + 1)}>Next<ArrowRight/></Button>
        : saved
          ? <Button type="button" disabled={disabled} onClick={() => onConfirmSaved(week.draftRevision)}><Check/>Confirm plan</Button>
          : <Button type="button" disabled={disabled} onClick={() => void onConfirm()}>Save to confirm</Button>}
    </div>
  </section>;
}

function LookBack({ week, tasks, disabled, onOpen, onOutcome }: { week: WeekOverviewDto; tasks: Map<string, WeekTaskDto>; disabled: boolean; onOpen: (task: TaskResponse) => void; onOutcome: (goal: WeekGoalDto, outcome: string) => void }) {
  const back = week.lookBack;
  return <div className="grid gap-3">
    <h3 className="font-semibold">Last week, {dayLabel(back.weekStart)} – {dayLabel(back.weekEnd)}</h3>
    <ul className="flex flex-wrap gap-x-5 gap-y-1 text-sm" aria-label="Last week in numbers">
      <li>{back.completedCount} completed</li>
      {back.hadPlan && <><li>{back.assignedDone} of {back.assignedCount} planned tasks done</li><li>{back.unfinishedTaskIds.length} unfinished</li><li>{back.assignedCancelled} cancelled</li>
        <li>{back.goals.filter(goal => goal.outcome === "Achieved").length} goals achieved · {back.goals.filter(goal => goal.outcome === "LetGo").length} let go</li></>}
    </ul>
    {!back.hadPlan && <p className="text-sm text-muted-foreground">Last week was not planned.</p>}
    <Goals goals={back.goals} disabled={disabled} onOutcome={onOutcome}/>
    {back.journalSummaryId && <p className="text-xs text-muted-foreground">From the weekly summary in your journal.</p>}
    {back.completedTaskIds.length > 0 && <details><summary className="cursor-pointer text-sm font-medium">Completed tasks<Count>{back.completedCount}</Count></summary>
      {back.journalSummaryId
        ? <ul className="grid gap-1 pt-1">{back.journalCompleted.map(item => <li key={item.taskId} className="flex items-center gap-2 rounded-lg border bg-card px-3 py-1.5 text-sm">
            <span className="min-w-0 flex-1 [overflow-wrap:anywhere]" dir="auto">{item.title}</span><span className="text-xs text-muted-foreground" dir="auto">{item.containerName}</span>
          </li>)}</ul>
        : <ul className="grid gap-1 pt-1">{back.completedTaskIds.map(id => <TaskLine key={id} item={tasks.get(id)} onOpen={onOpen}/>)}</ul>}
    </details>}
  </div>;
}

function Leftovers({ week, draft, tasks, today, onOpen, onEdit }: { week: WeekOverviewDto; draft: WeekDraftDto; tasks: Map<string, WeekTaskDto>; today: string; onOpen: (task: TaskResponse) => void; onEdit: (draft: WeekDraftDto) => void }) {
  const unfinished = week.lookBack.unfinishedTaskIds;
  if (unfinished.length === 0) return <EmptyState title="Nothing left over.">Last week's plan has no unfinished tasks.</EmptyState>;
  return <div className="grid gap-2">
    <p className="text-sm text-muted-foreground">Decide what happens to each unfinished task. Dropping a task from your plan never deletes or cancels it.</p>
    <ul className="grid gap-1.5" aria-label="Unfinished tasks">{unfinished.map(id => {
      const decided = draft.leftovers.find(item => item.taskId === id);
      const title = tasks.get(id)?.task.title ?? "task";
      return <TaskLine key={id} item={tasks.get(id)} onOpen={onOpen}>
        <div className="flex flex-wrap items-center gap-1">
          <NativeSelect size="sm" aria-label={`Decision for ${title}`} value={decided?.decision ?? ""} onChange={event => onEdit(decideLeftover(draft, id, event.target.value || null, decided?.laterDate ?? null))}>
            <NativeSelectOption value="">Not decided (stays off this week)</NativeSelectOption>
            {Object.entries(decisions).map(([value, text]) => <NativeSelectOption key={value} value={value}>{text}</NativeSelectOption>)}
          </NativeSelect>
          {decided?.decision === "Later" && <Input type="date" className="h-8 w-40" aria-label={`New planned date for ${title} (optional)`} min={today} value={decided.laterDate ?? ""}
            onChange={event => onEdit(decideLeftover(draft, id, "Later", event.target.value || null))}/>}
        </div>
      </TaskLine>;
    })}</ul>
  </div>;
}

function GoalStep({ week, draft, tasks, containers, onEdit }: { week: WeekOverviewDto; draft: WeekDraftDto; tasks: Map<string, WeekTaskDto>; containers: ContainerResponse[]; onEdit: (draft: WeekDraftDto) => void }) {
  const [title, setTitle] = React.useState("");
  const projects = containers.filter(container => container.kind === "Project" && !container.archived);
  const weekTasks = draft.assignments.map(item => tasks.get(item.taskId)).filter((item): item is WeekTaskDto => item !== undefined);
  const update = (goal: WeekDraftGoalDto, patch: Partial<WeekDraftGoalDto>) => onEdit({ ...draft, goals: draft.goals.map(item => item.id === goal.id ? { ...item, ...patch } : item) });
  const add = (goal: Omit<WeekDraftGoalDto, "id" | "outcome">) => onEdit({ ...draft, goals: [...draft.goals, { id: operationId(), outcome: "Pending", ...goal }] });
  const count = draft.goals.length;
  return <div className="grid gap-3">
    <p className="text-sm text-muted-foreground" role="status">{count < 2 ? "Two or three goals usually keep a week focused." : count > 3 ? "That is a lot for one week. Fewer goals are easier to finish, but it is your call." : "A good number of goals."}</p>
    <ul className="grid gap-2" aria-label="Goals for this week">{draft.goals.map((goal, index) => <li key={goal.id} className="grid gap-2 rounded-lg border p-2">
      <div className="flex flex-wrap items-end gap-2">
        <Field label={`Goal ${index + 1}`} className="min-w-56 flex-1"><Input dir="auto" value={goal.title} onChange={event => update(goal, { title: event.target.value })}/></Field>
        <Field label="Project"><NativeSelect size="sm" value={goal.projectId ?? ""} onChange={event => update(goal, { projectId: event.target.value || null })}>
          <NativeSelectOption value="">None</NativeSelectOption>
          {projects.map(project => <NativeSelectOption key={project.id} value={project.id}>{project.name}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove goal ${goal.title}`} onClick={() => onEdit({ ...draft, goals: draft.goals.filter(item => item.id !== goal.id) })}><X/></Button>
      </div>
      {weekTasks.length > 0 && <fieldset className="flex flex-wrap gap-x-3 gap-y-1"><legend className="text-xs text-muted-foreground">Linked tasks</legend>
        {weekTasks.map(item => <div key={item.task.id} className="flex items-center gap-1 text-sm"><label className="flex items-center gap-1.5"><input type="checkbox" checked={goal.taskIds.includes(item.task.id)}
          onChange={event => update(goal, { taskIds: event.target.checked ? [...goal.taskIds, item.task.id] : goal.taskIds.filter(id => id !== item.task.id) })}/>{item.task.title}</label><TaskViewAction task={item.task} title={item.task.title}/></div>)}
      </fieldset>}
    </li>)}</ul>
    <form className="flex gap-2" onSubmit={event => { event.preventDefault(); if (title.trim()) { add({ title: title.trim(), projectId: null, taskIds: [] }); setTitle(""); } }}>
      <Input dir="auto" aria-label="New goal" placeholder="Add a goal for this week" value={title} onChange={event => setTitle(event.target.value)}/>
      <Button type="submit" variant="outline"><Plus/>Add</Button>
    </form>
    {week.goalSuggestions.length > 0 && <section aria-labelledby="goal-ideas" className="grid gap-1 rounded-xl border bg-muted/40 p-3">
      <h4 id="goal-ideas" className="flex items-center gap-1.5 text-sm font-semibold"><Lightbulb className="size-4 text-primary"/>Ideas</h4>
      <ul className="grid gap-1">{week.goalSuggestions.map(item => <li key={item.title} className="flex items-center gap-2 text-sm">
        <span className="flex-1" dir="auto">{item.title}</span>
        <Button type="button" size="sm" variant="outline" aria-label={`Add goal ${item.title}`} onClick={() => add({ title: item.title, projectId: item.projectId, taskIds: [...item.taskIds] })}><Plus/>Add</Button>
      </li>)}</ul>
    </section>}
  </div>;
}

function PlanDays({ week, draft, tasks, today, onOpen, onEdit }: { week: WeekOverviewDto; draft: WeekDraftDto; tasks: Map<string, WeekTaskDto>; today: string; onOpen: (task: TaskResponse) => void; onEdit: (draft: WeekDraftDto) => void }) {
  const board = week.draftBoard;
  const dates = week.confirmed.days.map(day => day.date);
  const confirmedDay = new Map(week.confirmed.placements.map(item => [item.taskId, item.day]));
  const loads = new Map((board?.days ?? []).map(day => [day.date, day]));
  const fresh = board && JSON.stringify(week.draft?.assignments) === JSON.stringify(draft.assignments) && JSON.stringify(week.draft?.overrides) === JSON.stringify(draft.overrides);
  const drop = (day: string | null) => (event: React.DragEvent) => {
    const taskId = event.dataTransfer.getData(weekDragType);
    if (!taskId || (day !== null && day < today)) return;
    event.preventDefault();
    onEdit(placeTask(draft, taskId, day));
  };
  const setOverride = (date: string, total: number | null, work: number | null) => {
    const rest = draft.overrides.filter(item => item.date !== date);
    onEdit({ ...draft, overrides: total === null ? rest : [...rest, { date, totalMinutes: total, workMinutes: work }] });
  };
  const column = (day: string | null) => {
    const items = draft.assignments.filter(item => item.day === day);
    const load = day ? loads.get(day) : undefined;
    const override = day ? draft.overrides.find(item => item.date === day) : undefined;
    return <div key={day ?? "pool"} onDragOver={event => { if (day === null || day >= today) event.preventDefault(); }} onDrop={drop(day)}
      className={cn("grid min-w-0 grid-cols-[minmax(0,1fr)] content-start gap-1.5 rounded-xl border p-2", day === today && "border-primary", day !== null && day < today && "opacity-75")} aria-label={day ? dayLabel(day) : "No day yet"}>
      {day ? (fresh && load ? <DayHeading day={load}/> : <span className="text-sm font-semibold">{dayLabel(day)}</span>) : <span className="text-sm font-semibold">This week, no day yet</span>}
      {day && day >= today && <details className="text-xs"><summary className="cursor-pointer text-muted-foreground">{override ? "Custom time" : "Change time"}</summary>
        <div className="grid gap-1 pt-1">
          <Field label="Hours this day"><Input inputMode="decimal" className="h-8" value={toHours(override?.totalMinutes ?? load?.availableMinutes ?? null)}
            onChange={event => setOverride(day, toMinutes(event.target.value), override?.workMinutes ?? null)}/></Field>
          <Field label="Of which work"><Input inputMode="decimal" className="h-8" value={toHours(override?.workMinutes ?? null)}
            onChange={event => setOverride(day, override?.totalMinutes ?? load?.availableMinutes ?? 0, toMinutes(event.target.value))}/></Field>
          {override && <Button type="button" variant="link" size="xs" onClick={() => setOverride(day, null, null)}>Use the usual time</Button>}
        </div>
      </details>}
      <ol className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-1">{items.map(item => {
        const title = tasks.get(item.taskId)?.task.title ?? "task";
        const keptPast = (date: string) => date < today && confirmedDay.get(item.taskId) !== date;
        return <li key={item.taskId} draggable onDragStart={event => { event.dataTransfer.effectAllowed = "move"; event.dataTransfer.setData(weekDragType, item.taskId); }} className="min-w-0 list-none">
          <ul className="min-w-0"><TaskLine item={tasks.get(item.taskId)} onOpen={onOpen}>
            <div className="flex min-w-0 flex-wrap items-center gap-0.5">
              <NativeSelect size="sm" aria-label={`Day for ${title}`} value={item.day ?? ""} onChange={event => onEdit(placeTask(draft, item.taskId, event.target.value || null))}>
                <NativeSelectOption value="">No day</NativeSelectOption>
                {dates.map(date => <NativeSelectOption key={date} value={date} disabled={keptPast(date)}>{dayLabel(date)}</NativeSelectOption>)}
              </NativeSelect>
              <Button type="button" variant="ghost" size="icon-sm" aria-label={`Take ${title} off this week`} onClick={() => onEdit({ ...draft, assignments: draft.assignments.filter(other => other.taskId !== item.taskId), leftovers: draft.leftovers.filter(other => other.taskId !== item.taskId) })}><X/></Button>
            </div>
          </TaskLine></ul>
        </li>;
      })}</ol>
    </div>;
  };
  return <div className="@container grid min-w-0 gap-3">
    <p className="text-sm text-muted-foreground">Drag tasks between days, or choose a day from each task's menu. Warnings are advice only; nothing is moved for you.{!fresh && " Totals update when the draft is saved."}</p>
    <div className="grid min-w-0 grid-cols-1 gap-2 @min-[36rem]:grid-cols-2 @min-[54rem]:grid-cols-3">{dates.map(column)}</div>
    {column(null)}
    {week.candidates.length > 0 && <section aria-labelledby="week-candidates" className="grid gap-1 rounded-xl border bg-muted/40 p-3">
      <h4 id="week-candidates" className="flex items-center gap-1.5 text-sm font-semibold"><Lightbulb className="size-4 text-primary"/>Worth planning<Count>{week.candidates.length}</Count></h4>
      <ul className="grid gap-1">{week.candidates.filter(item => !draft.assignments.some(other => other.taskId === item.taskId)).map(item =>
        <TaskLine key={item.taskId} item={tasks.get(item.taskId)} onOpen={onOpen} badge={<Badge variant="outline">{candidateText[item.reason] ?? label(item.reason)}</Badge>}>
          <Button type="button" size="sm" variant="outline" aria-label={`Add ${tasks.get(item.taskId)?.task.title ?? "task"} to this week`} onClick={() => onEdit(placeTask(draft, item.taskId, null))}><Plus/>Add</Button>
        </TaskLine>)}</ul>
    </section>}
  </div>;
}

function ConfirmStep({ week, draft, tasks }: { week: WeekOverviewDto; draft: WeekDraftDto; tasks: Map<string, WeekTaskDto> }) {
  const board = week.draftBoard;
  const title = (id: string) => tasks.get(id)?.task.title ?? "A task";
  const overloaded = (board?.days ?? []).filter(day => day.overTotal || day.overWork);
  const unknown = (board?.days ?? []).reduce((sum, day) => sum + day.unknownEstimates, 0) + (board?.pool.unknownEstimates ?? 0);
  const changes = draft.leftovers.filter(item => item.decision === "Cancel" || (item.decision === "Later" && item.laterDate));
  return <div className="grid gap-2 text-sm">
    <p>{draft.goals.length} goals · {draft.assignments.filter(item => item.day !== null).length} tasks on days · {draft.assignments.filter(item => item.day === null).length} without a day</p>
    {board && <p className="tabular-nums">{minutesText(board.days.reduce((sum, day) => sum + day.plannedMinutes, 0))} estimated{unknown > 0 && `, ${unknown} tasks without an estimate`}</p>}
    {overloaded.length > 0 && <p className="text-destructive" role="status">Over capacity: {overloaded.map(day => dayLabel(day.date)).join(", ")}. You can still confirm.</p>}
    {changes.length > 0 && <div><p className="font-medium">Confirming also changes these tasks:</p>
      <ul className="list-disc pl-5">{changes.map(item => <li key={item.taskId}>{item.decision === "Cancel" ? `Cancel ${title(item.taskId)}` : `Plan ${title(item.taskId)} for ${item.laterDate}`}</li>)}</ul></div>}
    {week.state === "Confirmed" && <p className="text-muted-foreground">Days you have already opened keep your own choices; you will be shown what the new plan would change.</p>}
  </div>;
}
