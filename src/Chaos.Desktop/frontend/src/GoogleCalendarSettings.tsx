import React from "react";
import { trackSync } from "./syncActivity";
import { calendarsRpc, googleCalendarRpc, type CalendarSourceDto, type GoogleAccountDto, type GoogleCalendarChoiceDto } from "#neoastra";
import { Link2, LogIn, RefreshCw, Unplug } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, InlineError } from "@/components/common";
import { Input } from "@/components/ui/input";

type Api = Pick<typeof googleCalendarRpc, "status" | "setup" | "connect" | "calendars" | "choose" | "disconnect">;
type SourceApi = Pick<typeof calendarsRpc, "update" | "refresh">;

const when = (instant: string | null) => instant ? new Date(instant).toLocaleString() : "never";

/** One chosen calendar: colour, busy choice and when it last updated. Name and choice come from the account's list. */
function ChosenRow({ source, api, onUpdated, onError }: { source: CalendarSourceDto; api: SourceApi; onUpdated: (source: CalendarSourceDto) => void; onError: (message: string) => void }) {
  const [working, setWorking] = React.useState(false);

  async function save(patch: Partial<{ color: string; countsAsBusy: boolean }>) {
    setWorking(true);
    try {
      const result = await api.update({
        sourceId: source.id, expectedVersion: source.version, name: source.name, color: patch.color ?? source.color,
        countsAsBusy: patch.countsAsBusy ?? source.countsAsBusy, enabled: source.enabled, ownerEmail: source.ownerEmail,
      });
      if (result.ok && result.source) onUpdated(result.source); else onError(result.error?.message ?? "The calendar was not saved.");
    } catch (e) { onError(String(e)); } finally { setWorking(false); }
  }

  async function refresh() {
    setWorking(true);
    try {
      const result = await trackSync("calendar", api.refresh({ sourceId: source.id, onlyDue: false }));
      const fresh = result.sources.find(item => item.id === source.id);
      if (fresh) onUpdated(fresh);
      if (!result.ok) onError(result.error?.message ?? "Could not refresh.");
    } catch (e) { onError(String(e)); } finally { setWorking(false); }
  }

  return <li data-google-calendar={source.id} className="grid gap-1 rounded-md border p-2">
    <div className="flex flex-wrap items-center gap-2">
      <input type="color" aria-label={`Colour of ${source.name}`} className="size-6 shrink-0 cursor-pointer rounded border bg-transparent p-0" value={source.color} disabled={working}
        onChange={event => void save({ color: event.target.value })}/>
      <span dir="auto" className="text-sm font-medium">{source.name}</span>
      <label className="ml-auto inline-flex items-center gap-1.5 text-sm"><input type="checkbox" className="accent-primary" checked={source.countsAsBusy} disabled={working}
        onChange={event => void save({ countsAsBusy: event.target.checked })}/>Counts as busy time</label>
      <Button type="button" size="sm" variant="outline" disabled={working} aria-label={`Refresh ${source.name} now`} onClick={() => void refresh()}><RefreshCw/>Refresh now</Button>
    </div>
    <p className="text-xs text-muted-foreground">Last updated: {when(source.lastSuccessUtc)}</p>
    {source.lastError && <p role="alert" className="text-xs text-destructive">Could not update: {source.lastError}</p>}
  </li>;
}

function AccountCard({ account, api, sourceApi, onChanged, onReplace, onRemoved, onError }: {
  account: GoogleAccountDto; api: Api; sourceApi: SourceApi; onChanged: () => void; onReplace: (account: GoogleAccountDto) => void; onRemoved: (id: string) => void; onError: (message: string) => void;
}) {
  const [choices, setChoices] = React.useState<readonly GoogleCalendarChoiceDto[] | null>(null);
  const [choosing, setChoosing] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  const [confirming, setConfirming] = React.useState(false);
  const needsSignIn = account.needsSignIn || !account.signedInHere;

  async function reload() {
    const result = await api.status({});
    const fresh = result.accounts.find(item => item.id === account.id);
    if (fresh) onReplace(fresh);
    onChanged();
  }

  async function signIn() {
    setWorking(true);
    try {
      const result = await api.connect({});
      if (result.ok && result.account) { onReplace(result.account); onChanged(); } else onError(result.error?.message ?? "Google sign-in did not finish.");
    } catch (e) { onError(String(e)); } finally { setWorking(false); }
  }

  async function openChoices() {
    setChoosing(true); setWorking(true);
    try {
      const result = await api.calendars({ accountId: account.id });
      if (result.ok) setChoices(result.calendars); else { onError(result.error?.message ?? "Could not list the calendars."); setChoosing(false); await reload(); }
    } catch (e) { onError(String(e)); setChoosing(false); } finally { setWorking(false); }
  }

  async function toggle(choice: GoogleCalendarChoiceDto, chosen: boolean) {
    setWorking(true);
    try {
      const result = await api.choose({ accountId: account.id, calendarId: choice.calendarId, name: choice.name, color: choice.source?.color ?? choice.color ?? null, chosen });
      if (!result.ok || !result.source) { onError(result.error?.message ?? "The calendar was not saved."); return; }
      const source = result.source;
      setChoices(current => (current ?? []).map(item => item.calendarId === choice.calendarId ? { ...item, chosen, source } : item));
      if (result.refreshError) onError(`Chosen, but the first update failed: ${result.refreshError}`);
      await reload();
    } catch (e) { onError(String(e)); } finally { setWorking(false); }
  }

  async function disconnect() {
    setWorking(true);
    try {
      const result = await api.disconnect({ accountId: account.id });
      if (!result.ok) { onError(result.error?.message ?? "Could not disconnect."); return; }
      if (!result.revoked) onError("Disconnected from Chaos, but Google could not be reached to revoke access. You can remove Chaos at myaccount.google.com/permissions.");
      onRemoved(account.id); onChanged();
    } catch (e) { onError(String(e)); } finally { setWorking(false); setConfirming(false); }
  }

  const chosen = account.calendars.filter(source => source.enabled);
  return <li data-google-account={account.id} className="grid gap-2 rounded-lg border p-3">
    <div className="flex flex-wrap items-center gap-2">
      <span dir="auto" className="text-sm font-semibold">{account.email}</span>
      <span className="ml-auto flex flex-wrap items-center gap-1">
        {needsSignIn && <Button type="button" size="sm" disabled={working} onClick={() => void signIn()}><LogIn/>{account.signedInHere ? "Sign in again" : "Sign in on this device"}</Button>}
        {!needsSignIn && <Button type="button" size="sm" variant="outline" disabled={working || choosing} onClick={() => void openChoices()}>Choose calendars</Button>}
        {confirming
          ? <><span className="text-sm">Disconnect {account.email} on every device?</span>
            <Button type="button" size="sm" variant="destructive" disabled={working} onClick={() => void disconnect()}>Yes, disconnect</Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setConfirming(false)}>Cancel</Button></>
          : <Button type="button" size="sm" variant="ghost" aria-label={`Disconnect ${account.email}`} onClick={() => setConfirming(true)}><Unplug/>Disconnect</Button>}
      </span>
    </div>
    {account.error && <p role="alert" className="text-xs text-destructive">{account.error}</p>}
    {!account.error && account.lastSuccessUtc && <p className="text-xs text-muted-foreground">Last updated: {when(account.lastSuccessUtc)}</p>}
    {choosing && choices && <fieldset className="grid gap-1 rounded-md border border-dashed p-2">
      <legend className="px-1 text-xs font-semibold">Show these calendars</legend>
      {choices.map(choice => <label key={choice.calendarId} className="inline-flex items-center gap-2 text-sm">
        <input type="checkbox" className="accent-primary" checked={choice.chosen} disabled={working} onChange={event => void toggle(choice, event.target.checked)}/>
        <span aria-hidden className="size-3 rounded-sm border" style={{ backgroundColor: choice.source?.color ?? choice.color ?? "#3b82f6" }}/>
        <span dir="auto">{choice.name}{choice.primary ? " (main)" : ""}</span>
      </label>)}
      <div><Button type="button" size="sm" variant="ghost" onClick={() => setChoosing(false)}>Done</Button></div>
    </fieldset>}
    {chosen.length === 0 ? <p className="text-sm text-muted-foreground">{needsSignIn ? "Sign in to choose calendars." : "No calendars chosen yet."}</p>
      : <ul className="grid gap-1.5" aria-label={`Calendars of ${account.email}`}>{chosen.map(source => <ChosenRow key={source.id} source={source} api={sourceApi} onError={onError}
          onUpdated={updated => { onReplace({ ...account, calendars: account.calendars.map(item => item.id === updated.id ? updated : item) }); onChanged(); }}/>)}</ul>}
  </li>;
}

/** One-time setup: paste the Desktop OAuth client from Google Cloud. Stored protected on this computer; the secret is never shown again. */
function ClientSetup({ api, onSaved, onError }: { api: Api; onSaved: () => void; onError: (message: string | null) => void }) {
  const [clientId, setClientId] = React.useState("");
  const [clientSecret, setClientSecret] = React.useState("");
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});
  const [saving, setSaving] = React.useState(false);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (saving) return;
    setSaving(true); setFieldErrors({}); onError(null);
    try {
      const result = await api.setup({ clientId, clientSecret });
      if (result.ok) { setClientSecret(""); onSaved(); return; }
      setFieldErrors(Object.fromEntries((result.error?.fields ?? []).map(field => [field.fieldPath, field.message])));
      onError(result.error?.message ?? "The Google client was not saved.");
    } catch (e) { onError(String(e)); } finally { setSaving(false); }
  }

  return <form className="grid gap-3 rounded-lg border border-dashed p-3" aria-label="Set up Google sign-in" onSubmit={event => void save(event)}>
    <h4 className="text-sm font-semibold">Set up Google sign-in (once)</h4>
    <p className="text-sm text-muted-foreground">In Google Cloud Console, turn on the Google Calendar API, then open APIs &amp; Services → Credentials → Create credentials → OAuth client ID and choose “Desktop app”. Paste the client ID and secret here.</p>
    <Field label="Client ID"><Input value={clientId} required autoComplete="off" spellCheck={false} aria-invalid={!!fieldErrors.clientId} placeholder="123456-abc.apps.googleusercontent.com" onChange={event => setClientId(event.target.value)}/></Field>
    {fieldErrors.clientId && <p className="text-xs text-destructive" role="alert">{fieldErrors.clientId}</p>}
    <Field label="Client secret"><Input type="password" value={clientSecret} required autoComplete="off" spellCheck={false} aria-invalid={!!fieldErrors.clientSecret} onChange={event => setClientSecret(event.target.value)}/></Field>
    {fieldErrors.clientSecret && <p className="text-xs text-destructive" role="alert">{fieldErrors.clientSecret}</p>}
    <div><Button type="submit" disabled={saving || !clientId.trim() || !clientSecret.trim()}>Save and continue</Button></div>
  </form>;
}

/**
 * Settings → Calendars → Google Calendar (PLAN2 §10.3): connect one or more Google accounts, choose calendars and see when each
 * last updated. Access is read-only and the sign-in happens in the person's own browser; the host keeps the token in protected
 * storage on this computer. A device that learns of an account through sync shows "Sign in on this device".
 */
export function GoogleCalendarSettings({ api = googleCalendarRpc, sourceApi = calendarsRpc, onChanged }: { api?: Api; sourceApi?: SourceApi; onChanged?: () => void }) {
  const [configured, setConfigured] = React.useState(true);
  const [accounts, setAccounts] = React.useState<GoogleAccountDto[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [connecting, setConnecting] = React.useState(false);

  React.useEffect(() => {
    let current = true;
    void api.status({}).then(result => {
      if (!current) return;
      setConfigured(result.configured);
      if (result.ok) setAccounts([...result.accounts]); else setError(result.error?.message ?? "Could not load Google accounts.");
    }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api]);

  async function connect() {
    if (connecting) return;
    setConnecting(true); setError(null);
    try {
      const result = await api.connect({});
      if (!result.ok || !result.account) { setError(result.error?.message ?? "Google sign-in did not finish."); return; }
      const account = result.account;
      setAccounts(current => [...(current ?? []).filter(item => item.id !== account.id), account]);
      onChanged?.();
    } catch (e) { setError(String(e)); } finally { setConnecting(false); }
  }

  return <section aria-label="Google Calendar" className="grid gap-3 rounded-xl border bg-card p-4">
    <div className="grid gap-0.5">
      <h3 className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground"><Link2/>Google Calendar</h3>
      <p className="text-sm text-muted-foreground">Show meetings from your Google calendars, read-only. Chaos can only read: it never changes your calendar. Sign-in happens in your web browser, and the access key stays on this computer in protected storage.</p>
    </div>
    {error && <InlineError>{error}</InlineError>}
    {accounts === null ? <p className="text-sm text-muted-foreground">Loading Google accounts…</p>
      : accounts.length === 0 ? <p className="text-sm text-muted-foreground">No Google account is connected.</p>
      : <ul className="grid gap-2" aria-label="Google accounts">{accounts.map(account => <AccountCard key={account.id} account={account} api={api} sourceApi={sourceApi} onError={setError}
          onChanged={() => onChanged?.()} onReplace={next => setAccounts(current => (current ?? []).map(item => item.id === next.id ? next : item))}
          onRemoved={id => setAccounts(current => (current ?? []).filter(item => item.id !== id))}/>)}</ul>}
    {configured
      ? <div className="flex flex-wrap items-center gap-2">
        <Button type="button" disabled={connecting} onClick={() => void connect()}>{accounts && accounts.length > 0 ? "Connect another Google account" : "Connect Google Calendar"}</Button>
        {connecting && <p role="status" className="text-sm text-muted-foreground">Finish signing in in your web browser, then come back here.</p>}
      </div>
      : <ClientSetup api={api} onSaved={() => setConfigured(true)} onError={setError}/>}
  </section>;
}
