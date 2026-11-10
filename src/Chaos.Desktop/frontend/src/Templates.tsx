import React from "react";
import { Icon } from "./Icon";
import { templateRpc, type TemplateResponse, type TemplatePayloadResponse } from "#neoastra";

const emptyPayload = (): TemplatePayloadResponse => ({ title: "", notes: "", priority: "Normal", estimateMinutes: null, category: "Personal", plannedDayOffset: null, plannedTime: null, plannedZoneId: null, dueDayOffset: null, dueTime: null, dueZoneId: null, subtasks: [] });
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
  return <section className="templates" aria-labelledby="templates-heading">
    <header className="page-header">
      <div><p className="eyebrow">Reusable tasks</p><h2 id="templates-heading">Templates</h2><p className="subtle">New tasks from a template are added to {containerName}.</p></div>
      {selected && <div className="header-actions"><button type="button" onClick={() => edit(null)}><Icon name="plus"/>New template</button></div>}
    </header>
    {error && <p className="banner error" role="alert">{error}</p>}
    <div className="templates-layout">
      <div className="stack">
        {items.length === 0 ? <div className="empty-state"><p className="empty-title">No templates yet.</p><p className="subtle">Build one with the form to reuse a task and its subtasks.</p></div>
          : <ul className="template-list">{items.map(item => <li key={item.id} className={`card template-card${selected?.id === item.id ? " selected" : ""}`}>
            <div className="template-info"><strong>{item.name}</strong><span className="subtle">{item.payload.title}{item.payload.subtasks.length > 0 ? ` · ${item.payload.subtasks.length} subtask${item.payload.subtasks.length === 1 ? "" : "s"}` : ""}</span></div>
            <div className="button-row"><button type="button" className="primary" disabled={busy} onClick={() => void instantiate(item)}>Create task</button><button type="button" disabled={busy} onClick={() => edit(item)}>Edit</button><button type="button" className="icon-button danger" aria-label={`Delete ${item.name}`} title="Delete" disabled={busy} onClick={() => void changeDeletion(item, false)}><Icon name="trash"/></button></div>
          </li>)}</ul>}
        {deleted.length > 0 && <details className="trash"><summary>Deleted templates ({deleted.length})</summary><ul className="trash-list">{deleted.map(item => <li key={item.id}><span>{item.name}</span> <button type="button" className="link-button" disabled={busy} onClick={() => void changeDeletion(item, true)}>Restore</button></li>)}</ul></details>}
      </div>
      <form className="card stack" onSubmit={e => void save(e)}>
        <h3>{selected ? "Edit template" : "New template"}</h3>
        <label className="field">Template name<input required value={name} onChange={e => setName(e.target.value)}/></label>
        <label className="field">Task title<input required value={payload.title} onChange={e => change({ title: e.target.value })}/></label>
        <label className="field">Task notes<textarea rows={3} value={payload.notes} onChange={e => change({ notes: e.target.value })}/></label>
        <div className="field-grid three">
          <label className="field">Priority<select value={payload.priority} onChange={e => change({ priority: e.target.value })}>{["None", "Low", "Normal", "High", "Urgent"].map(s => <option key={s}>{s}</option>)}</select></label>
          <label className="field">Estimate (minutes)<input type="number" min="0" value={payload.estimateMinutes ?? ""} onChange={e => change({ estimateMinutes: e.target.value ? Number(e.target.value) : null })}/></label>
          <label className="field">Category<select value={payload.category} onChange={e => change({ category: e.target.value })}><option>Personal</option><option>Work</option></select></label>
        </div>
        {(["planned", "due"] as const).map(kind => <fieldset key={kind} className="when"><legend>{kind === "planned" ? "Planned" : "Due"}</legend>
          <div className="field-grid three">
            <label className="field">Days after creation<input type="number" min="0" value={payload[`${kind}DayOffset`] ?? ""} onChange={e => change({ [`${kind}DayOffset`]: e.target.value === "" ? null : Number(e.target.value) })}/></label>
            <label className="field">Time<input type="time" value={payload[`${kind}Time`] ?? ""} onChange={e => change({ [`${kind}Time`]: e.target.value || null })}/></label>
            <label className="field">Time zone<input value={payload[`${kind}ZoneId`] ?? ""} onChange={e => change({ [`${kind}ZoneId`]: e.target.value || null })} placeholder="e.g. Africa/Cairo"/></label>
          </div>
        </fieldset>)}
        <fieldset className="when"><legend>Subtask structure</legend>
          {payload.subtasks.length > 0 && <ol className="template-parts">{payload.subtasks.map((part, index) => <li key={index} className="stack">
            <div className="inline-form"><label className="field grow">Title<input value={part.title} onChange={e => change({ subtasks: payload.subtasks.map((p, i) => i === index ? { ...p, title: e.target.value } : p) })}/></label><button type="button" className="icon-button danger" aria-label="Remove subtask" title="Remove subtask" onClick={() => change({ subtasks: payload.subtasks.filter((_, i) => i !== index) })}><Icon name="trash"/></button></div>
            <label className="field">Notes<textarea rows={2} value={part.notes} onChange={e => change({ subtasks: payload.subtasks.map((p, i) => i === index ? { ...p, notes: e.target.value } : p) })}/></label>
          </li>)}</ol>}
          <div className="inline-form"><label className="grow"><span className="sr-only">New subtask</span><input value={partTitle} placeholder="Add a subtask" onChange={e => setPartTitle(e.target.value)} onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); if (partTitle.trim()) { change({ subtasks: [...payload.subtasks, { title: partTitle, notes: "" }] }); setPartTitle(""); } } }}/></label><button type="button" disabled={!partTitle.trim()} onClick={() => { change({ subtasks: [...payload.subtasks, { title: partTitle, notes: "" }] }); setPartTitle(""); }}>Add subtask</button></div>
        </fieldset>
        <div className="form-actions"><button className="primary" disabled={busy}>Save template</button>{selected && <button type="button" onClick={() => edit(null)}>Cancel edit</button>}</div>
      </form>
    </div>
  </section>;
}
