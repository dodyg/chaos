import React from "react";
import { journalRpc, type CarryOverCandidateDto, type SummaryDetailDto, type SummaryHeaderDto, type SummarySettingsResponse, type SummaryTaskDto } from "#neoastra";
import { BookOpen, CalendarArrowUp, Check, RefreshCw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Count, EmptyState, Field, FieldGroup, InlineError, PageHeader } from "@/components/common";

const operationId = () => crypto.randomUUID();
const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

export const kindText: Record<string, string> = { Morning: "Morning plan", Daily: "Evening summary", Weekly: "Weekly summary" };
const sourceText: Record<string, string> = { MyDay: "My Day", Week: "week plan", Planned: "planned date" };
const reasonText: Record<string, string> = { Overdue: "overdue", Unfinished: "unfinished", DueNext: "due", PlannedNext: "planned in your week" };
const triggerText: Record<string, string> = { Scheduled: "scheduled", CatchUp: "made after Chaos was closed", Manual: "made by hand" };

/** A yyyy-MM-dd local date as a readable day (no time zone involved). */
export function dayText(date: string) {
  const [year, month, day] = date.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day)).toLocaleDateString(undefined, { timeZone: "UTC", weekday: "short", day: "numeric", month: "short", year: "numeric" });
}

function timeText(iso: string) {
  return new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

export function periodText(header: SummaryHeaderDto) {
  return header.kind === "Weekly" ? `${dayText(header.periodStart)} – ${dayText(header.periodEnd)}` : dayText(header.periodStart);
}

/** Plain counts in one line: "3 completed · 2 unfinished · 1 diary update". */
export function countsText(header: SummaryHeaderDto) {
  const c = header.counts;
  if (c.quiet) return "A quiet period: nothing recorded.";
  const parts = header.kind === "Morning"
    ? [`${c.selected} planned`, `${c.deadlines} due today`]
    : [`${c.stillDone} completed`, `${c.unfinished} unfinished`, `${c.diaryUpdates} diary ${c.diaryUpdates === 1 ? "update" : "updates"}`];
  return parts.join(" · ");
}

/**
 * The "summary ready" prompt (PLAN §12): the newest unseen summary per kind, shown once its
 * time has come and outside quiet hours. Opening or dismissing it marks it seen.
 */
export function SummaryPrompts({ api = journalRpc, intervalMs = 60_000, onOpen }: { api?: typeof journalRpc; intervalMs?: number; onOpen: (summaryId: string) => void }) {
  const [prompts, setPrompts] = React.useState<SummaryHeaderDto[]>([]);
  const load = React.useCallback(async () => {
    try {
      const result = await api.prompts({});
      if (result.ok) setPrompts([...result.prompts]);
    } catch { /* the next poll retries */ }
  }, [api]);

  React.useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), intervalMs);
    return () => clearInterval(timer);
  }, [load, intervalMs]);

  async function dismiss(prompt: SummaryHeaderDto, open: boolean) {
    setPrompts(current => current.filter(item => item.id !== prompt.id));
    if (open) onOpen(prompt.id);
    try { await api.dismiss({ summaryId: prompt.id }); } catch { /* shown again on the next poll */ }
  }

  if (prompts.length === 0) return null;
  return <section aria-label="Summaries ready" className="grid gap-2">
    {prompts.map(prompt => <div key={prompt.id} role="status" className="flex flex-wrap items-center gap-2 rounded-xl border bg-card px-3 py-2 text-sm">
      <BookOpen className="size-4 text-primary"/>
      <span className="min-w-0 flex-1"><span className="font-medium">{kindText[prompt.kind]}</span> for {periodText(prompt)} is ready. <span className="text-muted-foreground">{countsText(prompt)}</span></span>
      <Button type="button" size="xs" onClick={() => void dismiss(prompt, true)}>Open</Button>
      <Button type="button" size="xs" variant="ghost" aria-label={`Dismiss ${kindText[prompt.kind]}`} onClick={() => void dismiss(prompt, false)}><X/></Button>
    </div>)}
  </section>;
}

/** The journal (PLAN §12): past summaries newest first, one summary in detail, generation and the schedule. */
export function Journal({ api = journalRpc, revision, today, openId, onOpenTask, onChanged }: {
  api?: typeof journalRpc; revision: number; today: string; openId: string | null; onOpenTask: (taskId: string) => void; onChanged: () => void;
}) {
  const [entries, setEntries] = React.useState<SummaryHeaderDto[]>([]);
  const [cursor, setCursor] = React.useState<string | null>(null);
  const [selectedId, setSelectedId] = React.useState<string | null>(openId);
  const [detail, setDetail] = React.useState<SummaryDetailDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);
  const [kind, setKind] = React.useState("Daily");
  const [date, setDate] = React.useState(today);
  const [local, setLocal] = React.useState(0);

  React.useEffect(() => { if (openId) setSelectedId(openId); }, [openId]);
  React.useEffect(() => { setDate(current => current || today); }, [today]);

  React.useEffect(() => {
    let current = true;
    void api.list({ cursor: null, limit: 20 }).then(result => {
      if (!current) return;
      if (result.ok) { setEntries([...result.entries]); setCursor(result.nextCursor); setSelectedId(id => id ?? result.entries[0]?.id ?? null); }
      else setError(result.error?.message ?? "Could not load the journal.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, revision, local]);

  React.useEffect(() => {
    if (!selectedId) { setDetail(null); return; }
    let current = true;
    void api.get({ summaryId: selectedId }).then(result => {
      if (!current) return;
      if (result.ok) setDetail(result.summary); else setError(result.error?.message ?? "Could not open the summary.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api, selectedId, revision, local]);

  async function more() {
    if (!cursor) return;
    const result = await api.list({ cursor, limit: 20 });
    if (result.ok) { setEntries(current => [...current, ...result.entries]); setCursor(result.nextCursor); }
    else setError(result.error?.message ?? "Could not load older summaries.");
  }

  async function generate(kindValue: string, dateValue: string) {
    if (working) return;
    setWorking(true); setError(null);
    try {
      const result = await api.generate({ operationId: operationId(), kind: kindValue, date: dateValue });
      if (!result.ok || !result.summary) throw new Error(result.error?.message ?? "Could not make the summary.");
      setSelectedId(result.summary.header.id);
      setLocal(value => value + 1);
    } catch (e) { setError(e instanceof Error ? e.message : "Could not make the summary."); }
    finally { setWorking(false); }
  }

  return <section aria-labelledby="journal-title" className="grid gap-5">
    <PageHeader eyebrow="Journal" title="Journal" titleId="journal-title" subtitle="Plain summaries built from your own lists and history, on this device only."/>
    {error && <InlineError>{error}</InlineError>}
    <form className="flex flex-wrap items-end gap-2" onSubmit={event => { event.preventDefault(); void generate(kind, date); }}>
      <Field label="Summary"><NativeSelect value={kind} onChange={event => setKind(event.target.value)}>
        {Object.entries(kindText).map(([value, text]) => <NativeSelectOption key={value} value={value}>{text}</NativeSelectOption>)}
      </NativeSelect></Field>
      <Field label="Date"><Input type="date" value={date} max={today} onChange={event => setDate(event.target.value)}/></Field>
      <Button disabled={working || !date}><RefreshCw/>Make summary now</Button>
    </form>
    <div className="grid gap-5 md:grid-cols-[15rem_1fr]">
      <nav aria-label="Past summaries">
        {entries.length === 0
          ? <EmptyState title="No summaries yet.">Your first summary appears at the evening summary time, or make one now.</EmptyState>
          : <ol className="grid gap-1">{entries.map(entry => <li key={entry.id}>
              <button type="button" aria-current={entry.id === detail?.header.id || entry.id === selectedId ? "true" : undefined}
                className="grid w-full gap-0.5 rounded-lg border bg-card px-3 py-2 text-left text-sm hover:border-input aria-[current=true]:border-primary"
                onClick={() => setSelectedId(entry.id)}>
                <span className="font-medium">{kindText[entry.kind]}</span>
                <span className="text-xs text-muted-foreground">{periodText(entry)}</span>
                <span className="text-xs text-muted-foreground">{countsText(entry)}</span>
              </button>
            </li>)}</ol>}
        {cursor && <Button type="button" variant="ghost" className="mt-2 w-full" onClick={() => void more()}>Older summaries</Button>}
      </nav>
      {detail
        ? <SummaryDetail detail={detail} api={api} working={working} onRevision={setSelectedId} onRegenerate={() => void generate(detail.header.kind, detail.header.periodStart)}
            onOpenTask={onOpenTask} onCarried={() => { onChanged(); setLocal(value => value + 1); }}/>
        : entries.length > 0 && <p className="text-sm text-muted-foreground">Choose a summary.</p>}
    </div>
    <SummarySchedule api={api}/>
  </section>;
}

function TaskItem({ task, children, onOpen }: { task: SummaryTaskDto; children?: React.ReactNode; onOpen: (taskId: string) => void }) {
  return <li className="flex flex-wrap items-center gap-2 rounded-lg border bg-card px-3 py-1.5 text-sm">
    <button type="button" className="min-w-0 flex-1 text-left [overflow-wrap:anywhere] hover:underline" dir="auto" onClick={() => onOpen(task.taskId)}>{task.title}</button>
    {children}
  </li>;
}

function Section({ title, count, children }: { title: string; count?: number; children: React.ReactNode }) {
  return <section className="grid gap-1.5" aria-label={title}>
    <h4 className="text-sm font-semibold">{title}{count !== undefined && <Count>{count}</Count>}</h4>
    {children}
  </section>;
}

function SummaryDetail({ detail, api, working, onRevision, onRegenerate, onOpenTask, onCarried }: {
  detail: SummaryDetailDto; api: typeof journalRpc; working: boolean; onRevision: (id: string) => void; onRegenerate: () => void; onOpenTask: (taskId: string) => void; onCarried: () => void;
}) {
  const { header, facts } = detail;
  const byContainer = new Map<string, typeof facts.completions[number][]>();
  for (const item of facts.completions) {
    const key = item.task.containerName || "Inbox";
    byContainer.set(key, [...(byContainer.get(key) ?? []), item]);
  }
  const next = header.kind === "Weekly" ? "next week" : "tomorrow";

  return <article aria-label={`${kindText[header.kind]} ${periodText(header)}`} className="grid content-start gap-4">
    <header className="grid gap-1">
      <h3 className="text-lg font-semibold">{kindText[header.kind]} · {periodText(header)}</h3>
      <p className="text-xs text-muted-foreground">
        {header.partial ? `Through ${timeText(header.capturedThroughUtc)}` : header.kind === "Morning" ? `As of ${timeText(header.capturedThroughUtc)}` : "Whole period"}
        {" · "}{triggerText[header.trigger]}{detail.revisions.length > 1 && ` · version ${header.revision} of ${detail.revisions.length}`}
      </p>
      <div className="flex flex-wrap items-center gap-2">
        {detail.revisions.length > 1 && <NativeSelect size="sm" aria-label="Version" value={header.id} onChange={event => onRevision(event.target.value)}>
          {detail.revisions.map(item => <NativeSelectOption key={item.id} value={item.id}>Version {item.revision}, {new Date(item.generatedAtUtc).toLocaleString()}</NativeSelectOption>)}
        </NativeSelect>}
        <Button type="button" size="sm" variant="outline" disabled={working} onClick={onRegenerate}><RefreshCw/>Make it again</Button>
      </div>
    </header>
    {header.counts.quiet && <EmptyState title="A quiet period.">Nothing was completed, planned or written down.</EmptyState>}

    {header.kind === "Morning" && <>
      {facts.selected.length > 0 && <Section title="Planned for today" count={facts.selected.length}>
        <ul className="grid gap-1">{facts.selected.map(task => <TaskItem key={task.taskId} task={task} onOpen={onOpenTask}/>)}</ul>
      </Section>}
      {facts.deadlines.length > 0 && <Section title="Due today" count={facts.deadlines.length}>
        <ul className="grid gap-1">{facts.deadlines.map(task => <TaskItem key={task.taskId} task={task} onOpen={onOpenTask}>{task.dueTime && <span className="text-xs">{task.dueTime}</span>}</TaskItem>)}</ul>
      </Section>}
      {facts.overdueCount > 0 && <p className="text-sm">{facts.overdueCount} overdue {facts.overdueCount === 1 ? "task" : "tasks"}.</p>}
      {facts.followUps.length > 0 && <Section title="Follow-ups due" count={facts.followUps.length}>
        <ul className="grid gap-1">{facts.followUps.map(item => <TaskItem key={item.task.taskId} task={item.task} onOpen={onOpenTask}>{item.person && <span className="text-xs text-muted-foreground">with <bdi>{item.person}</bdi></span>}</TaskItem>)}</ul>
      </Section>}
    </>}

    {header.kind !== "Morning" && <>
      <Section title="Completed" count={header.counts.stillDone}>
        {facts.completions.length === 0 ? <p className="text-sm text-muted-foreground">Nothing completed.</p>
          : [...byContainer.entries()].map(([container, items]) => <div key={container} className="grid gap-1">
              <h5 className="text-xs font-medium text-muted-foreground" dir="auto">{container}</h5>
              <ul className="grid gap-1">{items.map(item => <TaskItem key={`${item.task.taskId}-${item.completedAtUtc}`} task={item.task} onOpen={onOpenTask}>
                {item.reopenedAfter && <span className="text-xs text-muted-foreground">reopened later</span>}
              </TaskItem>)}</ul>
            </div>)}
      </Section>
      {facts.projects.length > 0 && <Section title="Projects moved">
        <ul className="grid gap-1 text-sm">{facts.projects.map(project => <li key={project.projectId} dir="auto">
          {project.name}: {project.doneBefore} of {project.countedBefore} → {project.doneAfter} of {project.countedAfter} done
        </li>)}</ul>
      </Section>}
      {facts.week && <Section title="Your week plan">
        <p className="text-sm">{facts.week.doneCount} of {facts.week.assignedCount} planned tasks done{facts.week.cancelledCount > 0 && `, ${facts.week.cancelledCount} cancelled`}.</p>
        {facts.week.goals.length > 0 && <ul className="grid gap-0.5 text-sm">{facts.week.goals.map(goal => <li key={goal.goalId} dir="auto">
          {goal.title}: {goal.countedCount > 0 ? `${goal.doneCount} of ${goal.countedCount}` : "assessed by you"}{goal.outcome !== "Pending" && ` (${goal.outcome === "LetGo" ? "let go" : "achieved"})`}
        </li>)}</ul>}
      </Section>}
      <CarryOver detail={detail} api={api} next={next} onOpenTask={onOpenTask} onCarried={onCarried}/>
      {facts.waiting.length > 0 && <Section title="Waiting on someone" count={facts.waiting.length}>
        <ul className="grid gap-1">{facts.waiting.map(item => <TaskItem key={item.task.taskId} task={item.task} onOpen={onOpenTask}>
          {item.person && <span className="text-xs text-muted-foreground"><bdi>{item.person}</bdi></span>}
          {item.followUpDueAtUtc && <span className="text-xs text-muted-foreground">follow up {new Date(item.followUpDueAtUtc).toLocaleDateString()}</span>}
        </TaskItem>)}</ul>
      </Section>}
      {facts.deadlines.length > 0 && <Section title={`Due ${next}`} count={facts.deadlines.length}>
        <ul className="grid gap-1">{facts.deadlines.map(task => <TaskItem key={task.taskId} task={task} onOpen={onOpenTask}>{task.dueDate && <span className="text-xs">{dayText(task.dueDate)}{task.dueTime && ` ${task.dueTime}`}</span>}</TaskItem>)}</ul>
      </Section>}
      {facts.diaryUpdateCount > 0 && <Section title="Diary updates" count={facts.diaryUpdateCount}>
        <ul className="grid gap-1">{facts.diaryUpdates.map(item => <li key={item.entryId} className="grid gap-0.5 rounded-lg border bg-card px-3 py-1.5 text-sm">
          <button type="button" className="text-left font-medium hover:underline" dir="auto" onClick={() => onOpenTask(item.taskId)}>{item.taskTitle}</button>
          <span className="text-muted-foreground [overflow-wrap:anywhere]" dir="auto">{item.excerpt}</span>
        </li>)}</ul>
      </Section>}
      {facts.suggested.length > 0 && <Section title={`Suggested start for ${next}`}>
        <ul className="grid gap-1">{facts.suggested.map(item => <TaskItem key={item.task.taskId} task={item.task} onOpen={onOpenTask}><span className="text-xs text-muted-foreground">{reasonText[item.reason]}</span></TaskItem>)}</ul>
        <p className="text-xs text-muted-foreground">Suggestions are never added by themselves.</p>
      </Section>}
    </>}
  </article>;
}

/** Previewed, explicit carry-over of unfinished tasks to the next My Day or next week's draft. */
function CarryOver({ detail, api, next, onOpenTask, onCarried }: { detail: SummaryDetailDto; api: typeof journalRpc; next: string; onOpenTask: (taskId: string) => void; onCarried: () => void }) {
  const unfinished = detail.facts.unfinished;
  const [preview, setPreview] = React.useState<{ target: string; targetDate: string; candidates: CarryOverCandidateDto[] } | null>(null);
  const [chosen, setChosen] = React.useState<ReadonlySet<string>>(new Set());
  const [message, setMessage] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);

  React.useEffect(() => { setPreview(null); setMessage(null); setError(null); }, [detail.header.id]);

  async function open() {
    setError(null); setMessage(null);
    const result = await api.previewCarryOver({ summaryId: detail.header.id });
    if (!result.ok || !result.target || !result.targetDate) { setError(result.error?.message ?? "Could not prepare carry-over."); return; }
    setPreview({ target: result.target, targetDate: result.targetDate, candidates: [...result.candidates] });
    setChosen(new Set(result.candidates.filter(item => item.stillOpen && !item.alreadyPlanned).map(item => item.task.taskId)));
  }

  async function carry() {
    if (!preview || chosen.size === 0 || working) return;
    setWorking(true); setError(null);
    try {
      const result = await api.carryOver({ operationId: operationId(), summaryId: detail.header.id, taskIds: [...chosen] });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not carry over.");
      setMessage(result.target === "Week"
        ? `${result.added} added to the plan draft for the week of ${dayText(result.targetDate!)}. Confirm it in My Week.`
        : `${result.added} added to My Day for ${dayText(result.targetDate!)}.`);
      setPreview(null);
      onCarried();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not carry over."); }
    finally { setWorking(false); }
  }

  if (unfinished.length === 0) return null;
  return <Section title="Planned but not finished" count={unfinished.length}>
    {!preview && <ul className="grid gap-1">{unfinished.map(item => <TaskItem key={item.task.taskId} task={item.task} onOpen={onOpenTask}><span className="text-xs text-muted-foreground">{sourceText[item.source]}</span></TaskItem>)}</ul>}
    {preview && <fieldset className="grid gap-1">
      <legend className="pb-1 text-sm">Carry to {preview.target === "Week" ? `the week of ${dayText(preview.targetDate)} (as a draft)` : `My Day on ${dayText(preview.targetDate)}`}:</legend>
      {preview.candidates.map(item => <label key={item.task.taskId} className="flex items-center gap-2 rounded-lg border bg-card px-3 py-1.5 text-sm">
        <input type="checkbox" disabled={!item.stillOpen || item.alreadyPlanned} checked={chosen.has(item.task.taskId)}
          onChange={event => setChosen(current => { const copy = new Set(current); if (event.target.checked) copy.add(item.task.taskId); else copy.delete(item.task.taskId); return copy; })}/>
        <span className="min-w-0 flex-1" dir="auto">{item.task.title}</span>
        {!item.stillOpen && <span className="text-xs text-muted-foreground">finished or deleted</span>}
        {item.alreadyPlanned && <span className="text-xs text-muted-foreground">already planned</span>}
      </label>)}
    </fieldset>}
    {error && <InlineError>{error}</InlineError>}
    {message && <p role="status" className="text-sm">{message}</p>}
    <div className="flex gap-2">
      {preview
        ? <><Button type="button" size="sm" disabled={working || chosen.size === 0} onClick={() => void carry()}><Check/>Carry {chosen.size} over</Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setPreview(null)}>Cancel</Button></>
        : <Button type="button" size="sm" variant="outline" onClick={() => void open()}><CalendarArrowUp/>Carry over to {next}…</Button>}
    </div>
    <p className="text-xs text-muted-foreground">Carrying over adds the tasks to your plan only; their planned and due dates stay as they are.</p>
  </Section>;
}

/** When summaries are made: morning, evening and weekly. An empty time or day turns that one off. */
function SummarySchedule({ api }: { api: typeof journalRpc }) {
  const [settings, setSettings] = React.useState<SummarySettingsResponse | null>(null);
  const [draft, setDraft] = React.useState({ morningTime: "", eveningTime: "", weeklyDay: "" });
  const [status, setStatus] = React.useState<string | null>(null);

  const adopt = React.useCallback((result: SummarySettingsResponse) => {
    setSettings(result);
    setDraft({ morningTime: result.morningTime ?? "", eveningTime: result.eveningTime ?? "", weeklyDay: result.weeklyDay ?? "" });
  }, []);

  React.useEffect(() => {
    void api.settings({}).then(result => { if (result.ok) adopt(result); else setStatus(result.error?.message ?? "Could not load the schedule."); });
  }, [api, adopt]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!settings?.version) return;
    const result = await api.updateSettings({ operationId: operationId(), expectedVersion: settings.version, morningTime: draft.morningTime || null, eveningTime: draft.eveningTime || null, weeklyDay: draft.weeklyDay || null });
    if (result.ok) { adopt(result); setStatus("Schedule saved."); } else setStatus(result.error?.message ?? "Could not save the schedule.");
  }

  return <form onSubmit={event => void save(event)}>
    <FieldGroup legend="When summaries are made">
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Morning plan"><Input type="time" value={draft.morningTime} onChange={event => setDraft({ ...draft, morningTime: event.target.value })}/></Field>
        <Field label="Evening summary"><Input type="time" value={draft.eveningTime} onChange={event => setDraft({ ...draft, eveningTime: event.target.value })}/></Field>
        <Field label="Weekly summary on"><NativeSelect value={draft.weeklyDay} onChange={event => setDraft({ ...draft, weeklyDay: event.target.value })}>
          <NativeSelectOption value="">Off</NativeSelectOption>
          {weekdays.map(day => <NativeSelectOption key={day} value={day}>{day}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Button disabled={!settings}>Save</Button>
      </div>
      <p className="text-xs text-muted-foreground">Clear a time to stop that summary. The weekly summary covers the week that has just ended, at the evening time. Prompts wait until quiet hours end. Summaries missed while Chaos was closed are made once when it starts.</p>
      {status && <p role="status" className="text-sm">{status}</p>}
    </FieldGroup>
  </form>;
}
