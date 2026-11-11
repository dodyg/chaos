import React from "react";
import { recurrenceRpc, taskRpc, type RecurrenceGetResponse, type RecurrenceRuleDto, type TaskOperationResponse, type TaskResponse } from "#neoastra";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, FieldGroup } from "@/components/common";
import { deviceTimeZones, ZoneSelect, type TimeZones } from "./ZoneSelect";

type Props = { task: TaskResponse; timeZones?: TimeZones; revision: number; busy: boolean; onChanged: (task: TaskResponse) => void; onError: (message: string) => void; api?: typeof recurrenceRpc; taskApi?: typeof taskRpc };
const names = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const localDate = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; };
const initial = (task: TaskResponse, preferred: string): RecurrenceRuleDto => ({ mode: "FixedSchedule", frequency: "Day", interval: 1,
  anchorDate: task.plannedDate ?? task.dueDate ?? localDate(), anchorTime: task.plannedTime ?? task.dueTime,
  zoneId: task.plannedZoneId ?? task.dueZoneId ?? preferred, weekdays: [],
  monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null, endKind: "Never", count: null, untilDate: null });

export function RecurrenceEditor({ task, timeZones: zoneList, revision, busy, onChanged, onError, api = recurrenceRpc, taskApi = taskRpc }: Props) {
  const timeZones = zoneList ?? deviceTimeZones();
  const [detail, setDetail] = React.useState<RecurrenceGetResponse | null>(null);
  const [rule, setRule] = React.useState<RecurrenceRuleDto>(() => initial(task, timeZones.preferred));
  const edited = React.useRef(false);
  // The Settings zone can arrive after the editor opened; a new rule follows it until the user edits.
  React.useEffect(() => {
    if (detail && !detail.rule && !edited.current && !task.plannedZoneId && !task.dueZoneId) setRule(current => ({ ...current, zoneId: timeZones.preferred }));
  }, [timeZones.preferred, detail]);
  const [preview, setPreview] = React.useState<readonly string[]>([]);
  const [pending, setPending] = React.useState(false);
  const [moveDate, setMoveDate] = React.useState("");
  const [moveTime, setMoveTime] = React.useState("");
  const generation = React.useRef(0);
  React.useEffect(() => {
    const current = ++generation.current;
    setDetail(null); setPreview([]); setRule(initial(task, timeZones.preferred)); edited.current = false;
    api.get({ taskId: task.id }).then(result => {
      if (generation.current !== current) return;
      if (!result.ok) { onError(result.error?.message ?? "Could not load recurrence."); return; }
      setDetail(result); if (result.rule) setRule(result.rule);
    }).catch((error: unknown) => { if (generation.current === current) onError(String(error)); });
    return () => { generation.current++; };
  }, [task.id, revision, api]);
  function change(patch: Partial<RecurrenceRuleDto>) { edited.current = true; setRule(current => ({ ...current, ...patch })); setPreview([]); }
  async function execute(action: () => Promise<TaskOperationResponse>) {
    setPending(true);
    try {
      const current = await taskApi.get({ taskId: task.id });
      if (!current.ok || !current.task) throw new Error(current.error?.message ?? "Could not load the task.");
      const fields: (keyof TaskResponse)[] = ["title", "notes", "priority", "estimateMinutes", "category", "plannedDate", "plannedTime", "plannedZoneId", "dueDate", "dueTime", "dueZoneId"];
      if (fields.some(field => current.task?.[field] !== task[field])) throw new Error("Save your task edits before changing recurrence.");
      const result = await action(); if (!result.ok || !result.task) throw new Error(result.error?.message ?? "Could not save recurrence."); onChanged(result.task);
    }
    catch (error) { onError(error instanceof Error ? error.message : String(error)); }
    finally { setPending(false); }
  }
  async function showPreview() {
    setPending(true);
    try {
      const year = Number(rule.anchorDate.slice(0, 4));
      const through = `${String(Math.min(9999, year + 20)).padStart(4, "0")}-12-31`;
      const result = await api.preview({ rule, through, limit: 8 });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not preview recurrence.");
      setPreview(result.slots);
    } catch (error) { onError(error instanceof Error ? error.message : String(error)); }
    finally { setPending(false); }
  }
  const disabled = busy || pending || !detail;
  const request = () => ({ operationId: crypto.randomUUID(), taskId: task.id, expectedVersion: task.version });
  const active = detail?.occurrences.find(item => item.taskId === task.id && item.state === "Active");
  return <section className="grid gap-4" aria-label="Recurrence">
    <p className="text-sm text-muted-foreground">A series keeps one active task. Completing or skipping it creates at most one successor.</p>
    {detail?.ended && <p role="status">This series has ended. Its history is kept below.</p>}
    {!detail?.ended && <>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Repeat from"><NativeSelect value={rule.mode} onChange={e => change({ mode: e.target.value, weekdays: [], monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null })}><NativeSelectOption value="FixedSchedule">Fixed schedule</NativeSelectOption><NativeSelectOption value="AfterCompletion">Completion</NativeSelectOption></NativeSelect></Field>
        <Field label="Frequency"><NativeSelect value={rule.frequency} onChange={e => change({ frequency: e.target.value, weekdays: [], monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null })}>{["Day", "Week", "Month", "Year"].map(value => <NativeSelectOption key={value}>{value}</NativeSelectOption>)}</NativeSelect></Field>
        <Field label="Every"><Input type="number" min="1" max="999" value={rule.interval} onChange={e => change({ interval: Number(e.target.value) })}/></Field>
        <Field label="Start date"><Input type="date" value={rule.anchorDate} onChange={e => change({ anchorDate: e.target.value })}/></Field>
        <Field label="Time (optional)"><Input type="time" value={rule.anchorTime ?? ""} onChange={e => change({ anchorTime: e.target.value || null })}/></Field>
        <Field label="Time zone"><ZoneSelect value={rule.zoneId} preferred={timeZones.preferred} zones={timeZones.zones} onChange={zoneId => change({ zoneId })}/></Field>
      </div>
      {rule.mode === "FixedSchedule" && rule.frequency === "Week" && <FieldGroup legend="Weekdays"><div className="flex flex-wrap gap-2">{names.map((name, day) => <label key={name} className="flex gap-1 text-sm"><input type="checkbox" checked={rule.weekdays.includes(day)} onChange={e => change({ weekdays: e.target.checked ? [...rule.weekdays, day].sort() : rule.weekdays.filter(value => value !== day) })}/>{name}</label>)}</div><p className="text-xs text-muted-foreground">Leave empty to use the start date’s weekday.</p></FieldGroup>}
      {rule.mode === "FixedSchedule" && ["Month", "Year"].includes(rule.frequency) && <FieldGroup legend="Calendar pattern"><div className="grid grid-cols-2 gap-3">
        {rule.frequency === "Year" && <Field label="Month (optional)"><Input type="number" min="1" max="12" value={rule.monthOfYear ?? ""} onChange={e => change({ monthOfYear: e.target.value ? Number(e.target.value) : null })}/></Field>}
        <Field label="Day of month (optional)"><Input type="number" min="1" max="31" value={rule.monthDay ?? ""} onChange={e => change({ monthDay: e.target.value ? Number(e.target.value) : null, ordinal: null, ordinalWeekday: null })}/></Field>
        <Field label="Ordinal weekday"><NativeSelect value={rule.ordinal ?? ""} onChange={e => change({ ordinal: e.target.value ? Number(e.target.value) : null, ordinalWeekday: e.target.value ? (rule.ordinalWeekday ?? 1) : null, monthDay: null })}><NativeSelectOption value="">Use day of month</NativeSelectOption>{[1, 2, 3, 4, 5, -1].map(n => <NativeSelectOption key={n} value={String(n)}>{n === -1 ? "Last" : String(n)}</NativeSelectOption>)}</NativeSelect></Field>
        {rule.ordinal !== null && <Field label="Weekday"><NativeSelect value={String(rule.ordinalWeekday ?? 1)} onChange={e => change({ ordinalWeekday: Number(e.target.value) })}>{names.map((name, day) => <NativeSelectOption key={name} value={String(day)}>{name}</NativeSelectOption>)}</NativeSelect></Field>}
      </div><p className="text-xs text-muted-foreground">Fixed schedules skip months without the selected day. Completion-relative month and year rules clamp to the last valid day.</p></FieldGroup>}
      <FieldGroup legend="End"><div className="grid grid-cols-2 gap-3"><Field label="End condition"><NativeSelect value={rule.endKind} onChange={e => change({ endKind: e.target.value, count: e.target.value === "Count" ? 1 : null, untilDate: e.target.value === "UntilDate" ? rule.anchorDate : null })}><NativeSelectOption value="Never">Never</NativeSelectOption><NativeSelectOption value="Count">After occurrences</NativeSelectOption><NativeSelectOption value="UntilDate">On date</NativeSelectOption></NativeSelect></Field>{rule.endKind === "Count" && <Field label="Occurrences"><Input type="number" min="1" value={rule.count ?? ""} onChange={e => change({ count: Number(e.target.value) })}/></Field>}{rule.endKind === "UntilDate" && <Field label="Until (inclusive)"><Input type="date" value={rule.untilDate ?? ""} onChange={e => change({ untilDate: e.target.value || null })}/></Field>}</div></FieldGroup>
      <div className="flex flex-wrap gap-2"><Button type="button" variant="outline" disabled={disabled} onClick={() => void showPreview()}>Preview</Button><Button type="button" disabled={disabled || (!!detail?.rule && !active)} onClick={() => void execute(() => (detail?.rule ? api.revise : api.start)({ ...request(), rule }))}>{detail?.rule ? "Change this and future" : "Start repeating"}</Button></div>
      {preview.length > 0 && <div><p className="text-sm font-semibold">Preview {rule.mode === "AfterCompletion" && "(assuming completion at each shown time)"}</p><ol className="list-inside list-disc text-sm">{preview.map(slot => <li key={slot}>{slot.replace("T", " ")}</li>)}</ol></div>}
      {active && <FieldGroup legend="This occurrence"><div className="grid grid-cols-2 gap-3"><Field label="Move to date"><Input type="date" value={moveDate} onChange={e => setMoveDate(e.target.value)}/></Field>{rule.anchorTime && <Field label="Time"><Input type="time" value={moveTime} onChange={e => setMoveTime(e.target.value)}/></Field>}</div><div className="flex flex-wrap gap-2 pt-2"><Button type="button" variant="outline" disabled={disabled || !moveDate || (!!rule.anchorTime && !moveTime)} onClick={() => void execute(() => api.move({ ...request(), date: moveDate, time: moveTime || null }))}>Move this one</Button><Button type="button" variant="outline" disabled={disabled} onClick={() => void execute(() => api.skip(request()))}>Skip this one</Button><Button type="button" variant="outline" disabled={disabled} onClick={() => { if (window.confirm("End future repetitions? The current task and history will remain.")) void execute(() => api.end(request())); }}>End series</Button></div></FieldGroup>}
    </>}
    {!!detail?.occurrences.length && <div><h3 className="font-semibold">Occurrence history</h3><ol className="grid gap-1 text-sm">{detail.occurrences.map(item => <li key={item.nominalKey}>{item.nominalKey.replace("T", " ")} — {item.state}{item.effectiveLocal !== item.nominalKey && ` (moved to ${item.effectiveLocal.replace("T", " ")})`}</li>)}</ol></div>}
  </section>;
}
