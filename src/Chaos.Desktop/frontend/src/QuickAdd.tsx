import React from "react";
import { captureRpc, type CaptureDraftDto, type CaptureItemDto, type CapturePreviewResponse, type CaptureTokenDto, type ContainerResponse } from "#neoastra";
import { CalendarDays, Clock, Hourglass, Plus, Repeat, Tag, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, InlineError, priorities } from "@/components/common";
import { createLatestRequest } from "./latestRequest";

export type CapturePreviewApi = Pick<typeof captureRpc, "preview">;

/** A recognized span the person turned back into plain text, kept while the same text stays at that place. */
type Exclusion = { start: number; end: number; text: string };

const languageNames: Record<string, string> = { en: "English", fr: "French", it: "Italian", es: "Spanish", id: "Indonesian", ar: "Arabic" };
const kindNames: Record<string, string> = {
  PlannedDate: "planned", DueDate: "due", Time: "time", Recurrence: "repeat", Reminder: "reminder", Estimate: "estimate", Tag: "tag",
  Priority: "priority", Container: "list", Space: "space", Waiting: "waiting", FollowUp: "follow-up", Delegation: "member",
};
const PREVIEW_DELAY_MS = 150;

/** Keeps exclusions whose text is still at the same offsets after an edit (PLAN §8: clicking a token disables it). */
export function keepExclusions(exclusions: readonly Exclusion[], text: string): Exclusion[] {
  return exclusions.filter(item => text.slice(item.start, item.end) === item.text);
}

const time = (value: string | null) => value ? value.slice(0, 5) : "";
const fromTime = (value: string) => value ? `${value}:00` : null;

type Props = {
  api?: CapturePreviewApi;
  containers: ContainerResponse[];
  /** The list new tasks go to when the text names none ("" = Inbox). */
  defaultContainerId: string;
  label: string;
  placeholder?: string;
  initialText?: string;
  allowBatch?: boolean;
  submitLabel?: string;
  inputRef?: React.Ref<HTMLTextAreaElement>;
  autoFocus?: boolean;
  /** Saves the confirmed drafts; resolves to an error message, or null once committed. */
  onSubmit: (drafts: CaptureDraftDto[], operationId: string) => Promise<string | null>;
  onCancel?: () => void;
};

/**
 * Quick add (PLAN §8): type or paste, see recognized parts as chips and the task fields as an
 * editable preview, then save exactly what is shown. Previews are debounced and stale
 * responses dropped; the OperationId stays the same across retries of the same drafts.
 */
export function QuickAdd({ api = captureRpc, containers, defaultContainerId, label, placeholder, initialText = "", allowBatch = true, submitLabel, inputRef, autoFocus, onSubmit, onCancel }: Props) {
  const id = React.useId();
  const [text, setText] = React.useState(initialText);
  const [exclusions, setExclusions] = React.useState<Exclusion[]>([]);
  const [preview, setPreview] = React.useState<CapturePreviewResponse | null>(null);
  const [overrides, setOverrides] = React.useState<Record<number, Partial<CaptureDraftDto>>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);
  const requests = React.useRef(createLatestRequest());
  const operation = React.useRef<string | null>(null);
  const exclusionKey = exclusions.map(item => `${item.start}:${item.end}`).join(",");

  React.useEffect(() => {
    operation.current = null; // the drafts changed, so a later save is a new operation
    if (!text.trim()) { requests.current.invalidate(); setPreview(null); return; }
    const isCurrent = requests.current.begin();
    const handle = setTimeout(() => {
      void api.preview({ text, excluded: exclusions.map(({ start, end }) => ({ start, end })) })
        .then(result => { if (isCurrent()) { setPreview(result); setError(result.ok ? null : result.error?.message ?? "Could not read that text."); } })
        .catch((e: unknown) => { if (isCurrent()) setError(e instanceof Error ? e.message : String(e)); });
    }, PREVIEW_DELAY_MS);
    return () => clearTimeout(handle);
  }, [text, exclusionKey, api]);

  React.useEffect(() => { operation.current = null; }, [overrides]);

  function change(next: string) {
    setText(next);
    setExclusions(current => keepExclusions(current, next));
    setError(null);
  }

  function toggle(item: CaptureItemDto, token: CaptureTokenDto) {
    const start = item.sourceStart + token.start;
    const end = item.sourceStart + token.end;
    setExclusions(current => current.some(entry => entry.start === start && entry.end === end)
      ? current.filter(entry => entry.start !== start || entry.end !== end)
      : [...current, { start, end, text: text.slice(start, end) }]);
    setOverrides({}); // the parse changes, so field edits made on the old parse no longer apply
  }

  function override(line: number, patch: Partial<CaptureDraftDto>) {
    setOverrides(current => ({ ...current, [line]: { ...current[line], ...patch } }));
  }

  const items = preview?.ok ? preview.items : [];
  const isBatch = !!preview?.ok && preview.isBatch;
  const draftFor = (item: CaptureItemDto): CaptureDraftDto => {
    const merged = { ...item.draft, ...overrides[item.lineNumber] };
    return { ...merged, containerId: merged.containerId ?? (defaultContainerId || null) };
  };
  const blocked = !preview?.ok || items.length === 0 || (preview.errors.length > 0) || (isBatch && !allowBatch)
    || items.some(item => !draftFor(item).title.trim());

  async function submit(event?: React.FormEvent) {
    event?.preventDefault();
    if (saving || blocked) return;
    operation.current ??= crypto.randomUUID();
    setSaving(true); setError(null);
    try {
      const failure = await onSubmit(items.map(draftFor), operation.current);
      if (failure) { setError(failure); return; }
      setText(""); setExclusions([]); setOverrides({}); setPreview(null); operation.current = null;
    } catch (e) { setError(e instanceof Error ? e.message : "The task could not be saved. Try again."); }
    finally { setSaving(false); }
  }

  function keyDown(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (event.nativeEvent.isComposing) return; // never intercept IME composition
    if (event.key === "Enter" && (event.ctrlKey || !event.shiftKey) && !(isBatch && !event.ctrlKey)) { event.preventDefault(); void submit(); }
    if (event.key === "Escape") { event.preventDefault(); if (onCancel) onCancel(); else change(""); }
  }

  const lines = Math.min(10, Math.max(1, text.split("\n").length));
  const excludedChips = exclusions.map(item => <Button key={`x${item.start}`} type="button" size="xs" variant="ghost" className="border border-dashed text-muted-foreground"
    title="Recognize this again" onClick={() => setExclusions(current => current.filter(entry => entry !== item))}>“{item.text}” as text</Button>);

  return <form className="grid gap-2" onSubmit={e => void submit(e)}>
    <div className="flex items-start gap-2 rounded-xl border border-input bg-card py-1.5 pr-1.5 pl-3.5 shadow-sm transition-[box-shadow,border-color] focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/30">
      <Plus className="mt-2.5 size-4 shrink-0 text-primary"/>
      <label htmlFor={`${id}-input`} className="sr-only">{label}</label>
      <textarea id={`${id}-input`} ref={inputRef} dir="auto" rows={lines} autoFocus={autoFocus} autoComplete="off" spellCheck
        className="min-h-9 min-w-0 flex-1 resize-none bg-transparent py-1.5 text-base outline-none placeholder:text-muted-foreground"
        value={text} onChange={e => change(e.target.value)} onKeyDown={keyDown} placeholder={placeholder}
        aria-describedby={`${id}-help`}/>
      <Button disabled={saving || blocked}>{submitLabel ?? (isBatch ? `Add ${items.length} tasks` : "Add task")}</Button>
    </div>
    <p id={`${id}-help`} className="sr-only">Dates, times, repeats, reminders, #tags, !priority and /list are recognized as you type. Enter saves; paste several lines to add several tasks; Ctrl+Enter saves a pasted list.</p>
    {error && <InlineError>{error}</InlineError>}

    {preview?.ok && text.trim() && <section aria-label="Quick add preview" aria-live="polite" className="grid gap-2 rounded-xl border bg-muted/40 p-3 text-sm">
      {isBatch && !allowBatch && <p role="alert" className="text-destructive">Select a single line to make one task.</p>}
      {preview.errors.length > 0 && <ul role="alert" aria-label="Problems in the pasted lines" className="grid gap-1 text-destructive">
        {preview.errors.map(problem => <li key={problem.lineNumber}>{problem.message}</li>)}
        <li className="text-muted-foreground">Nothing is saved until every line is fixed.</li>
      </ul>}
      {isBatch
        ? <ol className="grid gap-2" aria-label="Tasks to add">{items.map(item => <li key={item.lineNumber} className="grid gap-1 rounded-lg border bg-card p-2">
            <div className="flex items-center gap-2"><span className="text-xs text-muted-foreground tabular-nums">Line {item.lineNumber}</span>
              <Input dir="auto" aria-label={`Title on line ${item.lineNumber}`} className="h-8" value={draftFor(item).title} onChange={e => override(item.lineNumber, { title: e.target.value })}/></div>
            <TokenChips item={item} exclusions={exclusions} onToggle={token => toggle(item, token)}/>
            {item.draft.subtasks.length > 0 && <ul className="grid gap-0.5 pl-6 text-xs" aria-label={`Subtasks on line ${item.lineNumber}`}>
              {item.draft.subtasks.map((subtask, index) => <li key={index} className={cn(subtask.done && "text-muted-foreground line-through")} dir="auto">{subtask.title}</li>)}
            </ul>}
            <Warnings item={item}/>
          </li>)}</ol>
        : items[0] && <SingleDraft item={items[0]} draft={draftFor(items[0])} containers={containers} exclusions={exclusions}
            onToggle={token => toggle(items[0], token)} onChange={patch => override(items[0].lineNumber, patch)}/>}
      {excludedChips.length > 0 && <div className="flex flex-wrap gap-1.5" aria-label="Turned into plain text">{excludedChips}</div>}
      {items.some(item => item.languages.length > 0) && <p className="text-xs text-muted-foreground">Read as {[...new Set(items.flatMap(item => item.languages))].map(code => languageNames[code] ?? code).join(" and ")}.</p>}
      {onCancel && <div><Button type="button" size="sm" variant="outline" onClick={onCancel}>Cancel</Button></div>}
    </section>}
  </form>;
}

function TokenChips({ item, exclusions, onToggle }: { item: CaptureItemDto; exclusions: Exclusion[]; onToggle: (token: CaptureTokenDto) => void }) {
  if (item.tokens.length === 0) return null;
  return <div className="flex flex-wrap gap-1.5" aria-label="Recognized parts">{item.tokens.map(token => {
    const kept = token.state === "Kept";
    const off = exclusions.some(entry => entry.start === item.sourceStart + token.start && entry.end === item.sourceStart + token.end);
    return <Button key={`${token.start}-${token.end}`} type="button" size="xs" variant="outline" dir="auto" aria-pressed={!off}
      className={cn("gap-1", kept ? "border-dashed text-muted-foreground" : "border-primary/40 bg-brand-soft text-primary")}
      title={`${token.summary}. Click to keep “${token.text}” as plain text.`} onClick={() => onToggle(token)}>
      <span className="text-[0.65rem] tracking-wide uppercase opacity-70">{kindNames[token.kind] ?? token.kind}</span>{token.text}
    </Button>;
  })}</div>;
}

function Warnings({ item }: { item: CaptureItemDto }) {
  if (item.warnings.length === 0) return null;
  return <ul className="grid gap-0.5 text-xs text-amber-700 dark:text-amber-400" aria-label="Quick add notes">{item.warnings.map((warning, index) => <li key={index}>{warning.message}</li>)}</ul>;
}

function SingleDraft({ item, draft, containers, exclusions, onToggle, onChange }: { item: CaptureItemDto; draft: CaptureDraftDto; containers: ContainerResponse[]; exclusions: Exclusion[];
  onToggle: (token: CaptureTokenDto) => void; onChange: (patch: Partial<CaptureDraftDto>) => void }) {
  const repeat = item.tokens.find(token => token.kind === "Recurrence" && token.state === "Applied");
  const candidates = new Set(item.containerCandidates.map(candidate => candidate.id));
  const ordered = [...containers.filter(container => candidates.has(container.id)), ...containers.filter(container => !candidates.has(container.id))];
  return <div className="grid gap-3">
    <TokenChips item={item} exclusions={exclusions} onToggle={onToggle}/>
    <Warnings item={item}/>
    <div className="grid grid-cols-2 gap-3">
      <Field label="Title" className="col-span-2"><Input dir="auto" value={draft.title} onChange={e => onChange({ title: e.target.value })} placeholder="Add a few words for the title"/></Field>
      <Field label="List"><NativeSelect value={draft.containerId ?? ""} onChange={e => onChange({ containerId: e.target.value || null })}>
        {item.containerCandidates.length > 0 && !draft.containerId && <NativeSelectOption value="">Choose a list…</NativeSelectOption>}
        {ordered.map(container => <NativeSelectOption key={container.id} value={container.kind === "Inbox" ? "" : container.id}>{container.name}</NativeSelectOption>)}
      </NativeSelect></Field>
      <Field label="Priority"><NativeSelect value={draft.priority} onChange={e => onChange({ priority: e.target.value })}>{priorities.map(value => <NativeSelectOption key={value}>{value}</NativeSelectOption>)}</NativeSelect></Field>
      <Field label={<span className="inline-flex items-center gap-1"><CalendarDays className="size-3.5"/>Planned</span>}><div className="flex gap-1.5">
        <Input type="date" aria-label="Planned date" value={draft.plannedDate ?? ""} onChange={e => onChange({ plannedDate: e.target.value || null, plannedTime: e.target.value ? draft.plannedTime : null })}/>
        <Input type="time" aria-label="Planned time" value={time(draft.plannedTime)} disabled={!draft.plannedDate} onChange={e => onChange({ plannedTime: fromTime(e.target.value) })}/>
      </div></Field>
      <Field label={<span className="inline-flex items-center gap-1"><CalendarDays className="size-3.5"/>Due</span>}><div className="flex gap-1.5">
        <Input type="date" aria-label="Due date" value={draft.dueDate ?? ""} onChange={e => onChange({ dueDate: e.target.value || null, dueTime: e.target.value ? draft.dueTime : null })}/>
        <Input type="time" aria-label="Due time" value={time(draft.dueTime)} disabled={!draft.dueDate} onChange={e => onChange({ dueTime: fromTime(e.target.value) })}/>
      </div></Field>
      <Field label={<span className="inline-flex items-center gap-1"><Clock className="size-3.5"/>Estimate (minutes)</span>}><Input type="number" min="0" max="10080" value={draft.estimateMinutes ?? ""} onChange={e => onChange({ estimateMinutes: e.target.value === "" ? null : Number(e.target.value) })}/></Field>
      {draft.waitingOn !== null && <Field label={<span className="inline-flex items-center gap-1"><Hourglass className="size-3.5"/>Waiting on</span>}><Input dir="auto" value={draft.waitingOn} onChange={e => onChange({ waitingOn: e.target.value })}/></Field>}
      {draft.followUpDate !== null && <Field label="Follow up"><Input type="date" value={draft.followUpDate} onChange={e => onChange({ followUpDate: e.target.value || null })}/></Field>}
    </div>
    {(draft.recurrence || draft.reminders.length > 0 || draft.tagPaths.length > 0) && <div className="flex flex-wrap items-center gap-1.5">
      {draft.recurrence && <Removable icon={<Repeat className="size-3.5"/>} text={repeat?.summary ?? "Repeats"} name="repeat" onRemove={() => onChange({ recurrence: null })}/>}
      {draft.reminders.length > 0 && <Removable text={`${draft.reminders.length} reminder${draft.reminders.length === 1 ? "" : "s"}`} name="reminders" onRemove={() => onChange({ reminders: [] })}/>}
      {draft.tagPaths.map(path => { const existing = item.tags.find(tag => tag.path === path)?.existing ?? false;
        return <Removable key={path} icon={<Tag className="size-3.5"/>} text={`#${path}${existing ? "" : " (new)"}`} name={`tag ${path}`} onRemove={() => onChange({ tagPaths: draft.tagPaths.filter(other => other !== path) })}/>; })}
    </div>}
  </div>;
}

function Removable({ icon, text, name, onRemove }: { icon?: React.ReactNode; text: string; name: string; onRemove: () => void }) {
  return <span className="inline-flex items-center gap-1 rounded-md border bg-card py-0.5 pr-0.5 pl-2 text-xs" dir="auto">{icon}{text}
    <Button type="button" variant="ghost" size="icon-xs" aria-label={`Remove ${name}`} onClick={onRemove}><X/></Button></span>;
}
