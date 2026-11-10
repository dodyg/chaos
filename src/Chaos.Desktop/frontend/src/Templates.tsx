import React from "react";
import { Plus, Trash2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Textarea } from "@/components/ui/textarea";
import { Banner, DeletedItems, EmptyState, Field, FieldGroup, PageHeader, priorities } from "@/components/common";
import { templateRpc, type TemplateResponse, type TemplatePayloadResponse } from "#neoastra";

const emptyPayload = (): TemplatePayloadResponse => ({ title: "", notes: "", priority: "Normal", estimateMinutes: null, category: "Personal", plannedDayOffset: null, plannedTime: null, plannedZoneId: null, dueDayOffset: null, dueTime: null, dueZoneId: null, subtasks: [], recurrenceDefault: null, reminderOffsets: [] });
const operationId = () => crypto.randomUUID();

export function Templates({ containerId, containerName = "Inbox", onCreated }: { containerId: string; containerName?: string; onCreated: () => Promise<void> }) {
  const [items, setItems] = React.useState<TemplateResponse[]>([]);
  const [selected, setSelected] = React.useState<TemplateResponse | null>(null);
  const [name, setName] = React.useState("");
  const [payload, setPayload] = React.useState<TemplatePayloadResponse>(emptyPayload);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [partTitle, setPartTitle] = React.useState("");
  const [deleted, setDeleted] = React.useState<TemplateResponse[]>([]);
  async function refresh() {
    const result = await templateRpc.list({});
    if (!result.ok) throw new Error(result.error?.message ?? "Could not load templates.");
    const removed = await templateRpc.listDeleted({});
    if (!removed.ok) throw new Error(removed.error?.message ?? "Could not load deleted templates.");
    setItems([...result.templates]);
    setDeleted([...removed.templates]);
  }
  React.useEffect(() => { void refresh().catch((e: unknown) => setError(String(e))); }, []);
  function change(patch: Partial<TemplatePayloadResponse>) { setPayload(current => ({ ...current, ...patch })); }
  function edit(item: TemplateResponse | null) { setSelected(item); setName(item?.name ?? ""); setPayload(item?.payload ?? emptyPayload()); }
  async function save(e: React.FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try {
      const result = await templateRpc.save({ operationId: operationId(), templateId: selected?.id ?? null, expectedVersion: selected?.version ?? null, name, payload });
      if (!result.ok) throw new Error(result.error?.fields.map(f => f.message).join("; ") || result.error?.message || "Could not save template.");
      await refresh(); edit(null);
    } catch (e) { setError(e instanceof Error ? e.message : "Could not save template."); }
    finally { setBusy(false); }
  }
  async function instantiate(item: TemplateResponse) {
    setBusy(true); setError(null);
    try {
      const result = await templateRpc.instantiate({ operationId: operationId(), templateId: item.id, containerId: containerId || null });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not create task.");
      await onCreated();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not create task."); }
    finally { setBusy(false); }
  }
  async function changeDeletion(item: TemplateResponse, restore: boolean) {
    setBusy(true); setError(null);
    try {
      const request = { operationId: operationId(), templateId: item.id, expectedVersion: item.version };
      const result = restore ? await templateRpc.restore(request) : await templateRpc.delete(request);
      if (!result.ok || !result.template) throw new Error(result.error?.message ?? "Could not update template.");
      if (!restore && selected?.id === item.id) edit(null);
      await refresh();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not update template."); }
    finally { setBusy(false); }
  }
  const removeButton = "text-muted-foreground hover:bg-destructive/10 hover:text-destructive";
  return <section className="grid gap-5" aria-labelledby="templates-heading">
    <PageHeader eyebrow="Reusable tasks" title="Templates" titleId="templates-heading" subtitle={`New tasks from a template are added to ${containerName}.`}
      actions={selected && <Button type="button" variant="outline" onClick={() => edit(null)}><Plus/>New template</Button>}/>
    {error && <Banner tone="error">{error}</Banner>}
    <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.25fr)] items-start gap-5">
      <div className="grid content-start gap-3">
        {items.length === 0 ? <EmptyState title="No templates yet.">Build one with the form to reuse a task and its subtasks.</EmptyState>
          : <ul className="grid gap-2">{items.map(item => <li key={item.id}><Card className={cn("gap-3 p-3.5", selected?.id === item.id && "border-primary ring-1 ring-primary")}>
            <div className="grid gap-0.5"><strong className="font-semibold">{item.name}</strong><span className="text-muted-foreground">{item.payload.title}{item.payload.subtasks.length > 0 ? ` · ${item.payload.subtasks.length} subtask${item.payload.subtasks.length === 1 ? "" : "s"}` : ""}</span></div>
            <div className="flex flex-wrap items-center gap-2"><Button type="button" size="sm" disabled={busy} onClick={() => void instantiate(item)}>Create task</Button><Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => edit(item)}>Edit</Button><Button type="button" variant="ghost" size="icon-sm" className={removeButton} aria-label={`Delete ${item.name}`} title="Delete" disabled={busy} onClick={() => void changeDeletion(item, false)}><Trash2/></Button></div>
          </Card></li>)}</ul>}
        <DeletedItems noun="templates" items={deleted} name={item => item.name} busy={busy} onRestore={item => void changeDeletion(item, true)}/>
      </div>
      <Card className="py-5"><CardContent className="px-5"><form className="grid gap-4" onSubmit={e => void save(e)}>
        <h3 className="font-semibold">{selected ? "Edit template" : "New template"}</h3>
        <Field label="Template name"><Input required value={name} onChange={e => setName(e.target.value)}/></Field>
        <Field label="Task title"><Input required value={payload.title} onChange={e => change({ title: e.target.value })}/></Field>
        <Field label="Task notes"><Textarea rows={3} value={payload.notes} onChange={e => change({ notes: e.target.value })}/></Field>
        <div className="grid grid-cols-[repeat(auto-fit,minmax(8rem,1fr))] gap-3">
          <Field label="Priority"><NativeSelect value={payload.priority} onChange={e => change({ priority: e.target.value })}>{priorities.map(s => <NativeSelectOption key={s}>{s}</NativeSelectOption>)}</NativeSelect></Field>
          <Field label="Estimate (minutes)"><Input type="number" min="0" value={payload.estimateMinutes ?? ""} onChange={e => change({ estimateMinutes: e.target.value ? Number(e.target.value) : null })}/></Field>
          <Field label="Category"><NativeSelect value={payload.category} onChange={e => change({ category: e.target.value })}><NativeSelectOption>Personal</NativeSelectOption><NativeSelectOption>Work</NativeSelectOption></NativeSelect></Field>
        </div>
        {(["planned", "due"] as const).map(kind => <FieldGroup key={kind} legend={kind === "planned" ? "Planned" : "Due"}>
          <div className="grid grid-cols-[repeat(auto-fit,minmax(8rem,1fr))] gap-3">
            <Field label="Days after creation"><Input type="number" min="0" value={payload[`${kind}DayOffset`] ?? ""} onChange={e => change({ [`${kind}DayOffset`]: e.target.value === "" ? null : Number(e.target.value) })}/></Field>
            <Field label="Time"><Input type="time" value={payload[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null })}/></Field>
            <Field label="Time zone"><Input value={payload[`${kind}ZoneId`] ?? ""} onChange={e => change({ [`${kind}ZoneId`]: e.target.value || null })} placeholder="e.g. Africa/Cairo"/></Field>
          </div>
        </FieldGroup>)}
        <FieldGroup legend="Repeat by default">
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={payload.recurrenceDefault !== null} onChange={e => change({ recurrenceDefault: e.target.checked ? { mode: "FixedSchedule", frequency: "Day", interval: 1, anchorDate: "2000-01-01", anchorTime: null, zoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC", weekdays: [], monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null, endKind: "Never", count: null, untilDate: null } : null })}/>Create a repeating task</label>
          {payload.recurrenceDefault && <div className="grid grid-cols-2 gap-3 pt-2">
            <Field label="Repeat mode"><NativeSelect value={payload.recurrenceDefault.mode} onChange={e => change({ recurrenceDefault: { ...payload.recurrenceDefault!, mode: e.target.value, weekdays: [], monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null } })}><NativeSelectOption value="FixedSchedule">Fixed schedule</NativeSelectOption><NativeSelectOption value="AfterCompletion">Completion</NativeSelectOption></NativeSelect></Field>
            <Field label="Repeat frequency"><NativeSelect value={payload.recurrenceDefault.frequency} onChange={e => change({ recurrenceDefault: { ...payload.recurrenceDefault!, frequency: e.target.value, weekdays: [], monthDay: null, ordinal: null, ordinalWeekday: null, monthOfYear: null } })}>{["Day", "Week", "Month", "Year"].map(value => <NativeSelectOption key={value}>{value}</NativeSelectOption>)}</NativeSelect></Field>
            <Field label="Repeat every"><Input type="number" min="1" max="999" value={payload.recurrenceDefault.interval} onChange={e => change({ recurrenceDefault: { ...payload.recurrenceDefault!, interval: Number(e.target.value) } })}/></Field>
            <Field label="Repeat time zone"><Input value={payload.recurrenceDefault.zoneId} onChange={e => change({ recurrenceDefault: { ...payload.recurrenceDefault!, zoneId: e.target.value } })}/></Field>
          </div>}
          <p className="text-xs text-muted-foreground">The new series starts on the task's planned date, due date, or creation date. Further rule options are available on the task.</p>
        </FieldGroup>
        <FieldGroup legend="Subtask structure">
          {payload.subtasks.length > 0 && <ol className="grid gap-3">{payload.subtasks.map((part, index) => <li key={index} className="grid gap-3 border-b pb-3">
            <div className="flex items-end gap-2"><Field label="Title" className="flex-1"><Input value={part.title} onChange={e => change({ subtasks: payload.subtasks.map((p, i) => i === index ? { ...p, title: e.target.value } : p) })}/></Field><Button type="button" variant="ghost" size="icon" className={removeButton} aria-label="Remove subtask" title="Remove subtask" onClick={() => change({ subtasks: payload.subtasks.filter((_, i) => i !== index) })}><Trash2/></Button></div>
            <Field label="Notes"><Textarea rows={2} value={part.notes} onChange={e => change({ subtasks: payload.subtasks.map((p, i) => i === index ? { ...p, notes: e.target.value } : p) })}/></Field>
          </li>)}</ol>}
          <div className="flex gap-2"><label className="min-w-0 flex-1"><span className="sr-only">New subtask</span><Input value={partTitle} placeholder="Add a subtask" onChange={e => setPartTitle(e.target.value)} onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); if (partTitle.trim()) { change({ subtasks: [...payload.subtasks, { title: partTitle, notes: "" }] }); setPartTitle(""); } } }}/></label><Button type="button" variant="outline" disabled={!partTitle.trim()} onClick={() => { change({ subtasks: [...payload.subtasks, { title: partTitle, notes: "" }] }); setPartTitle(""); }}>Add subtask</Button></div>
        </FieldGroup>
        <div className="flex gap-2 pt-1"><Button disabled={busy}>Save template</Button>{selected && <Button type="button" variant="outline" onClick={() => edit(null)}>Cancel edit</Button>}</div>
      </form></CardContent></Card>
    </div>
  </section>;
}
