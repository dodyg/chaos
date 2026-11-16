import React from "react";
import { calendarsRpc, type CalendarSourceDto, type TaskErrorResponse } from "#neoastra";
import { CalendarDays, RefreshCw, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field, InlineError } from "@/components/common";

type Api = Pick<typeof calendarsRpc, "list" | "addFeed" | "update" | "remove" | "refresh">;

const when = (instant: string | null) => instant ? new Date(instant).toLocaleString() : "never";
const fieldErrorsOf = (error: TaskErrorResponse | null) => Object.fromEntries((error?.fields ?? []).map(field => [field.fieldPath, field.message]));

function SourceRow({ source, api, onUpdated, onRemoved, onError }: {
  source: CalendarSourceDto; api: Api; onUpdated: (source: CalendarSourceDto) => void; onRemoved: (id: string) => void; onError: (message: string) => void;
}) {
  const [name, setName] = React.useState(source.name);
  const [owner, setOwner] = React.useState(source.ownerEmail ?? "");
  const [confirming, setConfirming] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  React.useEffect(() => { setName(source.name); setOwner(source.ownerEmail ?? ""); }, [source.name, source.ownerEmail, source.version]);

  async function save(patch: Partial<{ name: string; color: string; countsAsBusy: boolean; enabled: boolean; ownerEmail: string }>) {
    setWorking(true);
    try {
      const result = await api.update({
        sourceId: source.id, expectedVersion: source.version, name: patch.name ?? source.name, color: patch.color ?? source.color,
        countsAsBusy: patch.countsAsBusy ?? source.countsAsBusy, enabled: patch.enabled ?? source.enabled, ownerEmail: (patch.ownerEmail ?? source.ownerEmail ?? "") || null,
      });
      if (result.ok && result.source) onUpdated(result.source); else onError(result.error?.message ?? "The calendar was not saved.");
    } catch (e) { onError(String(e)); } finally { setWorking(false); }
  }

  async function refresh() {
    setWorking(true);
    try {
      const result = await api.refresh({ sourceId: source.id, onlyDue: false });
      const fresh = result.sources.find(item => item.id === source.id);
      if (fresh) onUpdated(fresh);
      if (!result.ok) onError(result.error?.message ?? "Could not refresh.");
    } catch (e) { onError(String(e)); } finally { setWorking(false); }
  }

  async function remove() {
    setWorking(true);
    try {
      const result = await api.remove({ sourceId: source.id });
      if (result.ok) onRemoved(source.id); else onError(result.error?.message ?? "Could not remove it.");
    } catch (e) { onError(String(e)); } finally { setWorking(false); setConfirming(false); }
  }

  return <li data-calendar-source={source.id} className="grid gap-2 rounded-lg border p-3">
    <div className="flex flex-wrap items-center gap-2">
      <input type="color" aria-label={`Colour of ${source.name}`} className="size-7 shrink-0 cursor-pointer rounded border bg-transparent p-0" value={source.color} disabled={working}
        onChange={event => void save({ color: event.target.value })}/>
      <Input aria-label={`Name of ${source.name}`} className="h-8 max-w-64" value={name} maxLength={120} disabled={working} onChange={event => setName(event.target.value)}
        onBlur={() => { if (name.trim() && name !== source.name) void save({ name: name.trim() }); }}/>
      <span className="ml-auto flex flex-wrap items-center gap-1">
        <Button type="button" size="sm" variant="outline" disabled={working} aria-label={`Refresh ${source.name} now`} onClick={() => void refresh()}><RefreshCw/>Refresh now</Button>
        {confirming
          ? <><span className="text-sm">Remove “{source.name}” from every device?</span>
            <Button type="button" size="sm" variant="destructive" disabled={working} onClick={() => void remove()}>Yes, remove</Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setConfirming(false)}>Cancel</Button></>
          : <Button type="button" size="sm" variant="ghost" aria-label={`Remove ${source.name}`} onClick={() => setConfirming(true)}><Trash2/>Remove</Button>}
      </span>
    </div>
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1 text-sm">
      <label className="inline-flex items-center gap-1.5"><input type="checkbox" className="accent-primary" checked={source.countsAsBusy} disabled={working} onChange={event => void save({ countsAsBusy: event.target.checked })}/>Counts as busy time</label>
      <label className="inline-flex items-center gap-1.5"><input type="checkbox" className="accent-primary" checked={source.enabled} disabled={working} onChange={event => void save({ enabled: event.target.checked })}/>Show this calendar</label>
    </div>
    <Field label="Your email (to hide invitations you declined)">
      <Input type="email" className="h-8 max-w-80" value={owner} placeholder="Optional" disabled={working} onChange={event => setOwner(event.target.value)}
        onBlur={() => { if (owner.trim() !== (source.ownerEmail ?? "")) void save({ ownerEmail: owner.trim() }); }}/>
    </Field>
    <p className="text-xs text-muted-foreground">Last updated: {when(source.lastSuccessUtc)}</p>
    {source.lastError && <p role="alert" className="text-xs text-destructive">Could not update: {source.lastError}</p>}
  </li>;
}

/**
 * Settings → Calendars (PLAN2 §10.2): calendar feeds whose meetings show read-only in the calendar,
 * My Day and My Week. The pasted address is sent once and never shown again; the host keeps it in
 * protected storage, not in the database.
 */
export function CalendarSettings({ api = calendarsRpc, onChanged }: { api?: Api; onChanged?: () => void }) {
  const [sources, setSources] = React.useState<CalendarSourceDto[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [address, setAddress] = React.useState("");
  const [name, setName] = React.useState("");
  const [color, setColor] = React.useState("#3b82f6");
  const [busyTime, setBusyTime] = React.useState(true);
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});
  const [adding, setAdding] = React.useState(false);

  React.useEffect(() => {
    let current = true;
    void api.list({}).then(result => { if (!current) return; if (result.ok) setSources(result.sources.filter(item => item.provider === "Feed")); else setError(result.error?.message ?? "Could not load calendars."); })
      .catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api]);

  async function add(event: React.FormEvent) {
    event.preventDefault();
    if (adding) return;
    setAdding(true); setError(null); setNotice(null); setFieldErrors({});
    try {
      const result = await api.addFeed({ address, name, color, countsAsBusy: busyTime, ownerEmail: null });
      if (!result.ok || !result.source) {
        setFieldErrors(fieldErrorsOf(result.error));
        setError(result.error?.message ?? "The calendar was not added.");
        return;
      }
      const added = result.source;
      setSources(current => [...(current ?? []), added]);
      setAddress(""); setName("");
      setNotice(result.refreshError ? `Added, but the first update failed: ${result.refreshError}` : "Added. Its meetings now show in your calendar.");
      onChanged?.();
    } catch (e) { setError(String(e)); } finally { setAdding(false); }
  }

  return <section aria-label="Calendars" className="grid gap-3 rounded-xl border bg-card p-4">
    <div className="grid gap-0.5">
      <h3 className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground"><CalendarDays/>Calendars</h3>
      <p className="text-sm text-muted-foreground">Show meetings from other calendars, read-only. Busy meetings take time off your available hours in My Week and My Day. Paste the calendar's secret ICS address; Chaos keeps it in protected storage on this computer, not in your data.</p>
    </div>
    {error && <InlineError>{error}</InlineError>}
    {notice && <p role="status" className="text-sm text-muted-foreground">{notice}</p>}
    {sources === null ? <p className="text-sm text-muted-foreground">Loading calendars…</p>
      : sources.length === 0 ? <p className="text-sm text-muted-foreground">No calendars yet.</p>
      : <ul className="grid gap-2" aria-label="Calendar feeds">{sources.map(source => <SourceRow key={source.id} source={source} api={api} onError={setError}
          onUpdated={updated => { setSources(current => (current ?? []).map(item => item.id === updated.id ? updated : item)); onChanged?.(); }}
          onRemoved={id => { setSources(current => (current ?? []).filter(item => item.id !== id)); onChanged?.(); }}/>)}</ul>}
    <form className="grid gap-3 rounded-lg border border-dashed p-3" onSubmit={event => void add(event)} aria-label="Add calendar feed">
      <h4 className="text-sm font-semibold">Add calendar feed</h4>
      <Field label="Calendar address (https:// or webcal://)">
        <Input value={address} required autoComplete="off" spellCheck={false} aria-invalid={!!fieldErrors.address} placeholder="https://example.com/calendar.ics" onChange={event => setAddress(event.target.value)}/>
      </Field>
      {fieldErrors.address && <p className="text-xs text-destructive" role="alert">{fieldErrors.address}</p>}
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Name"><Input value={name} required maxLength={120} aria-invalid={!!fieldErrors.name} onChange={event => setName(event.target.value)}/></Field>
        <Field label="Colour"><input type="color" className="h-9 w-12 cursor-pointer rounded border bg-transparent p-0.5" value={color} onChange={event => setColor(event.target.value)}/></Field>
        <label className="inline-flex items-center gap-1.5 pb-2 text-sm"><input type="checkbox" className="accent-primary" checked={busyTime} onChange={event => setBusyTime(event.target.checked)}/>Counts as busy time</label>
        <Button type="submit" disabled={adding || !address.trim() || !name.trim()}>Add calendar</Button>
      </div>
      {fieldErrors.name && <p className="text-xs text-destructive" role="alert">{fieldErrors.name}</p>}
    </form>
  </section>;
}
