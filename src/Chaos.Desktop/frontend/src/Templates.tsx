import React from "react";
import { templateRpc, type TemplateResponse, type TemplatePayloadResponse } from "#neoastra";

const emptyPayload = (): TemplatePayloadResponse => ({ title: "", notes: "", priority: "Normal", estimateMinutes: null, category: "Personal", plannedDayOffset: null, plannedTime: null, plannedZoneId: null, dueDayOffset: null, dueTime: null, dueZoneId: null, subtasks: [] });
const operationId = () => crypto.randomUUID();

export function Templates({ containerId, onCreated }: { containerId: string; onCreated: () => Promise<void> }) {
  const [items, setItems] = React.useState<TemplateResponse[]>([]);
  const [selected, setSelected] = React.useState<TemplateResponse | null>(null);
  const [name, setName] = React.useState("");
  const [payload, setPayload] = React.useState<TemplatePayloadResponse>(emptyPayload);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [partTitle, setPartTitle] = React.useState("");
  const [deleted, setDeleted] = React.useState<TemplateResponse | null>(null);
  async function refresh() {
    const result = await templateRpc.list({});
    if (!result.ok) throw new Error(result.error?.message ?? "Could not load templates.");
    setItems([...result.templates]);
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
      setDeleted(restore ? null : result.template);
      if (!restore && selected?.id === item.id) edit(null);
      await refresh();
    } catch (e) { setError(e instanceof Error ? e.message : "Could not update template."); }
    finally { setBusy(false); }
  }
  return <section aria-labelledby="templates-heading"><h2 id="templates-heading">Templates</h2>
    {error && <p role="alert">{error}</p>}
    {deleted && <p>Template deleted. <button type="button" disabled={busy} onClick={() => void changeDeletion(deleted, true)}>Restore {deleted.name}</button></p>}
    <ul>{items.map(item => <li key={item.id}>{item.name} <button type="button" disabled={busy} onClick={() => edit(item)}>Edit</button> <button type="button" disabled={busy} onClick={() => void instantiate(item)}>Create task</button> <button type="button" disabled={busy} onClick={() => void changeDeletion(item, false)}>Delete</button></li>)}</ul>
    <h3>{selected ? "Edit template" : "New template"}</h3><form onSubmit={e => void save(e)}>
      <label>Template name<input required value={name} onChange={e => setName(e.target.value)}/></label>
      <label>Task title<input required value={payload.title} onChange={e => change({ title: e.target.value })}/></label>
      <label>Task notes<textarea value={payload.notes} onChange={e => change({ notes: e.target.value })}/></label>
      <label>Priority<select value={payload.priority} onChange={e => change({ priority: e.target.value })}>{["None", "Low", "Normal", "High", "Urgent"].map(s => <option key={s}>{s}</option>)}</select></label>
      <label>Estimate (minutes)<input type="number" min="0" value={payload.estimateMinutes ?? ""} onChange={e => change({ estimateMinutes: e.target.value ? Number(e.target.value) : null })}/></label>
      <label>Category<select value={payload.category} onChange={e => change({ category: e.target.value })}><option>Personal</option><option>Work</option></select></label>
      <div className="grid">{(["planned", "due"] as const).map(kind => <fieldset key={kind}><legend>{kind === "planned" ? "Planned" : "Due"}</legend>
        <label>Days after creation<input type="number" min="0" value={payload[`${kind}DayOffset`] ?? ""} onChange={e => change({ [`${kind}DayOffset`]: e.target.value === "" ? null : Number(e.target.value) })}/></label>
        <label>Time<input type="time" value={payload[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null })}/></label>
        <label>Time zone<input value={payload[`${kind}ZoneId`] ?? ""} onChange={e => change({ [`${kind}ZoneId`]: e.target.value || null })}/></label>
      </fieldset>)}</div>
      <fieldset><legend>Subtask structure</legend><ol>{payload.subtasks.map((part, index) => <li key={index}><label>Title<input value={part.title} onChange={e => change({ subtasks: payload.subtasks.map((p, i) => i === index ? { ...p, title: e.target.value } : p) })}/></label><label>Notes<textarea value={part.notes} onChange={e => change({ subtasks: payload.subtasks.map((p, i) => i === index ? { ...p, notes: e.target.value } : p) })}/></label><button type="button" onClick={() => change({ subtasks: payload.subtasks.filter((_, i) => i !== index) })}>Remove subtask</button></li>)}</ol>
        <label>New subtask<input value={partTitle} onChange={e => setPartTitle(e.target.value)}/></label><button type="button" disabled={!partTitle.trim()} onClick={() => { change({ subtasks: [...payload.subtasks, { title: partTitle, notes: "" }] }); setPartTitle(""); }}>Add subtask</button>
      </fieldset>
      <div className="row"><button disabled={busy}>Save template</button>{selected && <button type="button" onClick={() => edit(null)}>Cancel edit</button>}</div>
    </form>
  </section>;
}
