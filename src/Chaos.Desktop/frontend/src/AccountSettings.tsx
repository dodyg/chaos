import React from "react";
import { accountRpc, type AccountErrorDto, type AccountInfoDto, type ContentCountsDto, type JoinPreviewResponse, type ServiceAddressResponse } from "#neoastra";
import { CloudOff, LogIn, LogOut, MonitorSmartphone, Trash2, UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { InlineError } from "@/components/common";
import { Input } from "@/components/ui/input";
import { SyncPanel, type SyncApi } from "./SyncStatus";

type Api = Pick<typeof accountRpc, "status" | "account" | "signIn" | "cancelSignIn" | "leave" | "revokeDevice" | "address" | "setAddress" | "joinPreview" | "join" | "deleteAccount" | "reauthenticate">;

const units = ["bytes", "KB", "MB", "GB", "TB"];
export function formatBytes(bytes: number): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${unit === 0 ? value : value >= 100 ? Math.round(value) : Math.round(value * 10) / 10} ${units[unit]}`;
}

const lastSeen = (instant: string) => new Date(instant).toLocaleString();

/** One device row. Removing a device asks first; removing this computer also signs it out here. */
function DeviceRow({ device, busy, onRemove }: { device: AccountInfoDto["devices"][number]; busy: boolean; onRemove: (id: string) => void }) {
  const [confirming, setConfirming] = React.useState(false);
  return <li data-device={device.id} className="grid gap-1 rounded-md border p-2">
    <div className="flex flex-wrap items-center gap-2">
      <MonitorSmartphone className="size-4 text-muted-foreground" aria-hidden="true"/>
      <span dir="auto" className="text-sm font-medium">{device.name}</span>
      {device.current && <span className="rounded bg-muted px-1.5 py-0.5 text-xs">This computer</span>}
      <span className="ml-auto text-xs text-muted-foreground">Last seen {lastSeen(device.lastSeenUtc)}</span>
      {!confirming && <Button type="button" size="sm" variant="outline" disabled={busy} aria-label={`Remove ${device.name}`} onClick={() => setConfirming(true)}>Remove</Button>}
    </div>
    <p className="text-xs text-muted-foreground">{device.platform} · Chaos {device.clientVersion}</p>
    {confirming && <div role="group" aria-label={`Confirm removing ${device.name}`} className="flex flex-wrap items-center gap-2 text-sm">
      <span>{device.current ? "Remove this computer and sign out here?" : `Sign ${device.name} out of your account?`}</span>
      <Button type="button" size="sm" variant="destructive" disabled={busy} onClick={() => { setConfirming(false); onRemove(device.id); }}>Remove</Button>
      <Button type="button" size="sm" variant="ghost" onClick={() => setConfirming(false)}>Keep</Button>
    </div>}
  </li>;
}

const countsLine = (counts: ContentCountsDto) => `${counts.tasks} tasks, ${counts.lists} lists, ${counts.tags} tags, ${counts.pages} pages`;

/**
 * Joining the account after signing in (PLAN2 P14). The first device just starts syncing: its data becomes the account's. When the
 * account already has data the person chooses, with both sides' counts shown: merge this computer's data into the account (the
 * default whenever this computer has any), or replace it with the account's. Either way a safety backup is made first.
 */
function JoinPanel({ api, preview, onDone }: { api: Api; preview: JoinPreviewResponse; onDone: () => void }) {
  const [mode, setMode] = React.useState(preview.defaultMode);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [replaceConfirm, setReplaceConfirm] = React.useState(false);

  async function join() {
    setBusy(true); setError(null);
    try {
      const result = await api.join({ mode });
      if (result.ok) onDone(); else setError(result.error?.message ?? "Joining did not finish.");
    } catch (e) { setError(String(e)); } finally { setBusy(false); }
  }

  if (preview.case === "FirstDevice") {
    return <div role="group" aria-label="Start syncing" className="grid gap-2 rounded-md border bg-muted/40 p-3">
      <p className="text-sm font-medium">Start syncing this computer</p>
      <p className="text-sm text-muted-foreground">Your account has no data yet, so what is on this computer ({countsLine(preview.local)}) becomes your account's, and Chaos keeps it in step with your other devices.</p>
      {error && <InlineError>{error}</InlineError>}
      <div><Button type="button" size="sm" disabled={busy} onClick={() => void join()}>Start syncing</Button></div>
    </div>;
  }

  const hasLocal = preview.local.tasks + preview.local.lists + preview.local.tags + preview.local.pages > 0;
  return <div role="group" aria-label="Join your account" className="grid gap-3 rounded-md border bg-muted/40 p-3">
    <p className="text-sm font-medium">Your account already has data</p>
    <dl className="grid gap-1 text-sm">
      <div className="flex flex-wrap gap-2"><dt className="font-medium">On this computer:</dt><dd>{countsLine(preview.local)}</dd></div>
      <div className="flex flex-wrap gap-2"><dt className="font-medium">In your account:</dt><dd>{preview.account ? countsLine(preview.account) : "unknown"}</dd></div>
    </dl>
    <fieldset className="grid gap-2">
      <legend className="text-sm font-medium">What should happen to this computer's data?</legend>
      <label className="flex items-start gap-2 text-sm">
        <input type="radio" name="join-mode" className="mt-1" checked={mode === "Merge"} onChange={() => { setMode("Merge"); setReplaceConfirm(false); }}/>
        <span><strong>Merge this computer's data into the account</strong><br/><span className="text-muted-foreground">Keeps both. Tags are combined; two lists with the same name stay separate, the second renamed "(2)".</span></span>
      </label>
      <label className="flex items-start gap-2 text-sm">
        <input type="radio" name="join-mode" className="mt-1" checked={mode === "Replace"} onChange={() => setMode("Replace")}/>
        <span><strong>Replace this computer's data with the account's</strong><br/><span className="text-muted-foreground">What is only on this computer is removed from it.</span></span>
      </label>
    </fieldset>
    <p className="text-xs text-muted-foreground">Chaos makes a safety backup first. You can restore it from Settings → Data &amp; backups.</p>
    {error && <InlineError>{error}</InlineError>}
    {mode === "Replace" && hasLocal && replaceConfirm
      ? <div role="group" aria-label="Confirm replacing" className="flex flex-wrap items-center gap-2 text-sm">
        <span>Remove this computer's {preview.local.tasks} tasks and everything else here, and use the account's?</span>
        <Button type="button" size="sm" variant="destructive" disabled={busy} onClick={() => void join()}>Replace</Button>
        <Button type="button" size="sm" variant="ghost" onClick={() => setReplaceConfirm(false)}>Keep my data</Button>
      </div>
      : <div><Button type="button" size="sm" disabled={busy} onClick={() => { if (mode === "Replace" && hasLocal) setReplaceConfirm(true); else void join(); }}>{mode === "Merge" ? "Merge and start syncing" : "Replace and start syncing"}</Button></div>}
  </div>;
}

/** Sign out, asking what happens to this computer's data (PLAN2 §7.13 case 3). */
function LeaveControl({ busy, onLeave }: { busy: boolean; onLeave: (keepData: boolean) => void }) {
  const [asking, setAsking] = React.useState(false);
  if (!asking) return <Button type="button" size="sm" variant="outline" className="ml-auto" disabled={busy} onClick={() => setAsking(true)}><LogOut/>Sign out</Button>;
  return <div role="group" aria-label="Sign out" className="grid w-full gap-2 rounded-md border p-2 text-sm">
    <p>Sign out of your account on this computer. What should happen to the data here?</p>
    <div className="flex flex-wrap gap-2">
      <Button type="button" size="sm" disabled={busy} onClick={() => { setAsking(false); onLeave(true); }}>Keep data on this device</Button>
      <Button type="button" size="sm" variant="destructive" disabled={busy} onClick={() => { setAsking(false); onLeave(false); }}>Remove the account's data from this device</Button>
      <Button type="button" size="sm" variant="ghost" onClick={() => setAsking(false)}>Cancel</Button>
    </div>
    <p className="text-xs text-muted-foreground">Keeping it leaves Chaos working on this computer without syncing. Removing it empties this computer; your account keeps its copy.</p>
  </div>;
}

/**
 * Deleting the account (PLAN2 §8.6): a typed phrase, and a sign-in made in the last ten minutes. The service says when that is too old,
 * and then the person signs in again here and the deletion goes through. This computer's data is never touched.
 */
function DeleteAccountControl({ api, onDeleted }: { api: Api; onDeleted: () => void }) {
  const [open, setOpen] = React.useState(false);
  const [phrase, setPhrase] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [needsSignIn, setNeedsSignIn] = React.useState(false);

  async function remove() {
    setBusy(true); setError(null);
    try {
      const result = await api.deleteAccount({ confirmation: phrase.trim() });
      if (result.ok) { onDeleted(); return; }
      if (result.error?.kind === "FreshSignInRequired") setNeedsSignIn(true);
      setError(result.error?.message ?? "The account was not deleted.");
    } catch (e) { setError(String(e)); } finally { setBusy(false); }
  }

  async function signInAgain() {
    setBusy(true); setError(null);
    try {
      const result = await api.reauthenticate({});
      if (result.ok) { setNeedsSignIn(false); await remove(); return; }
      setError(result.error?.message ?? "Sign-in did not finish.");
    } catch (e) { setError(String(e)); } finally { setBusy(false); }
  }

  if (!open) return <div><Button type="button" size="sm" variant="outline" onClick={() => setOpen(true)}><Trash2/>Delete account…</Button></div>;
  return <form role="group" aria-label="Delete account" className="grid gap-2 rounded-md border border-destructive/50 p-3 text-sm" onSubmit={e => { e.preventDefault(); void remove(); }}>
    <p className="font-medium">Delete your account</p>
    <p>This removes your tasks, notes and other data from the online service, and every device is signed out. The data on this computer and your other devices stays where it is; each keeps working without syncing.</p>
    <label htmlFor="delete-phrase" className="font-medium">Type DELETE to confirm</label>
    <Input id="delete-phrase" className="max-w-48" autoComplete="off" spellCheck={false} value={phrase} onChange={e => setPhrase(e.target.value)}/>
    {error && <InlineError>{error}</InlineError>}
    <div className="flex flex-wrap gap-2">
      {needsSignIn
        ? <Button type="button" size="sm" variant="destructive" disabled={busy || phrase.trim() !== "DELETE"} onClick={() => void signInAgain()}>Sign in again to confirm</Button>
        : <Button type="submit" size="sm" variant="destructive" disabled={busy || phrase.trim() !== "DELETE"}>Delete my account</Button>}
      <Button type="button" size="sm" variant="ghost" onClick={() => { setOpen(false); setPhrase(""); setError(null); setNeedsSignIn(false); }}>Cancel</Button>
    </div>
  </form>;
}

const sourceNote: Record<string, string> = {
  Setting: "",
  Environment: "This address comes from the CHAOS_SERVICE_URL setting on this computer. Saving one here replaces it.",
  File: "This address comes from chaos-service.json next to Chaos. Saving one here replaces it.",
};

/** Where the online service is. Typed here so nobody has to edit a file or an environment variable; changing it needs a sign-out first. */
function ServiceAddress({ api, onChanged }: { api: Api; onChanged: () => void }) {
  const [info, setInfo] = React.useState<ServiceAddressResponse | null>(null);
  const [draft, setDraft] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);
  const [saved, setSaved] = React.useState(false);

  React.useEffect(() => {
    let current = true;
    void api.address({}).then(result => { if (current) { setInfo(result); setDraft(result.saved ?? result.address ?? ""); } }).catch((e: unknown) => { if (current) setError(String(e)); });
    return () => { current = false; };
  }, [api]);

  async function save(next: string) {
    setSaving(true); setError(null); setSaved(false);
    try {
      const result = await api.setAddress({ address: next });
      setInfo(result);
      if (result.ok) { setDraft(result.saved ?? result.address ?? ""); setSaved(true); onChanged(); } else setError(result.error ?? "The address was not saved.");
    } catch (e) { setError(String(e)); } finally { setSaving(false); }
  }

  const unchanged = draft.trim() === (info?.saved ?? info?.address ?? "");
  return <form className="grid gap-1.5" onSubmit={e => { e.preventDefault(); void save(draft); }}>
    <label htmlFor="service-address" className="text-sm font-medium">Service address</label>
    <div className="flex flex-wrap items-center gap-2">
      <Input id="service-address" className="min-w-64 flex-1" inputMode="url" autoComplete="off" spellCheck={false} placeholder="https://sync.example.com"
        value={draft} onChange={e => { setDraft(e.target.value); setSaved(false); }}/>
      <Button type="submit" size="sm" disabled={saving || unchanged}>Save</Button>
      {info?.saved && <Button type="button" size="sm" variant="ghost" disabled={saving} onClick={() => void save("")}>Clear</Button>}
    </div>
    {error && <InlineError>{error}</InlineError>}
    {saved && !error && <p role="status" className="text-xs text-muted-foreground">Saved.</p>}
    {info && sourceNote[info.source] && <p className="text-xs text-muted-foreground">{sourceNote[info.source]}</p>}
    <p className="text-xs text-muted-foreground">The web address of your Chaos online service. Ask whoever runs it if you are not sure.</p>
  </form>;
}

/**
 * Settings → Account (PLAN2 P12). Chaos works fully without an account; this is the only place that offers one, with the reason shown:
 * sync with your other devices. Sign-in happens in the person's own web browser and the host keeps the sign-in in protected
 * storage on this computer. A signed-in device lists the account's devices and can remove any of them.
 */
export function AccountSettings({ api = accountRpc, syncApi }: { api?: Api; syncApi?: SyncApi }) {
  const [configured, setConfigured] = React.useState(true);
  const [signedIn, setSignedIn] = React.useState<boolean | null>(null);
  const [email, setEmail] = React.useState<string | null>(null);
  const [host, setHost] = React.useState<string | null>(null);
  const [account, setAccount] = React.useState<AccountInfoDto | null>(null);
  const [problem, setProblem] = React.useState<AccountErrorDto | null>(null);
  const [signingIn, setSigningIn] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [preview, setPreview] = React.useState<JoinPreviewResponse | null>(null);

  const fail =React.useCallback((error: AccountErrorDto | null | undefined, fallback: string) => {
    const next = error ?? { kind: "Other", message: fallback };
    setProblem(next);
    if (next.kind === "SignInAgain") { setSignedIn(false); setAccount(null); }
  }, []);

  const loadPreview = React.useCallback(async () => {
    try {
      const result = await api.joinPreview({});
      setPreview(result.ok && result.case !== "AlreadyJoined" ? result : null);
    } catch { setPreview(null); }
  }, [api]);

  const loadAccount = React.useCallback(async () => {
    setBusy(true);
    try {
      const result = await api.account({});
      if (result.ok && result.account) { setAccount(result.account); setEmail(result.account.email); setProblem(null); await loadPreview(); } else fail(result.error, "Could not load your account.");
    } catch (e) { fail(null, String(e)); } finally { setBusy(false); }
  }, [api, fail, loadPreview]);

  const [addressVersion, setAddressVersion] = React.useState(0);
  React.useEffect(() => {
    let current = true;
    void api.status({}).then(result => {
      if (!current) return;
      setConfigured(result.configured); setSignedIn(result.signedIn); setEmail(result.email ?? null); setHost(result.serviceHost ?? null);
      if (!result.ok) fail(result.error, "Could not read the account status.");
      else if (result.signedIn) void loadAccount();
    }).catch((e: unknown) => { if (current) fail(null, String(e)); });
    return () => { current = false; };
  }, [api, fail, loadAccount, addressVersion]);

  async function signIn() {
    if (signingIn) return;
    setSigningIn(true); setProblem(null);
    try {
      const result = await api.signIn({});
      if (result.ok && result.account) { setAccount(result.account); setEmail(result.account.email); setSignedIn(true); await loadPreview(); } else fail(result.error, "Sign-in did not finish.");
    } catch (e) { fail(null, String(e)); } finally { setSigningIn(false); }
  }

  async function leave(keepData: boolean) {
    setBusy(true); setProblem(null);
    try {
      const result = await api.leave({ keepData });
      if (result.ok) { setSignedIn(result.signedIn); setEmail(null); setAccount(null); setPreview(null); } else fail(result.error, "Could not sign out.");
    } catch (e) { fail(null, String(e)); } finally { setBusy(false); }
  }

  async function removeDevice(id: string) {
    setBusy(true); setProblem(null);
    try {
      const result = await api.revokeDevice({ deviceId: id });
      if (!result.ok) { fail(result.error, "The device was not removed."); return; }
      if (result.account) setAccount(result.account); else { setSignedIn(false); setAccount(null); setEmail(null); }
    } catch (e) { fail(null, String(e)); } finally { setBusy(false); }
  }

  const signInAgain = problem?.kind === "SignInAgain";
  return <section aria-label="Account" className="grid gap-3 rounded-xl border bg-card p-4">
    <div className="grid gap-0.5">
      <h3 className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground"><UserRound/>Account</h3>
      <p className="text-sm text-muted-foreground">An account lets Chaos sync with your other devices. It is optional: everything works on this computer without one.</p>
    </div>
    {problem && <InlineError>{problem.message}</InlineError>}
    {signedIn === null ? <p className="text-sm text-muted-foreground">Loading account…</p>
      : !configured ? <div className="grid gap-3">
        <p className="flex items-center gap-2 text-sm text-muted-foreground"><CloudOff className="size-4" aria-hidden="true"/>No online service is set up yet. Enter its address to create an account or sign in.</p>
        <ServiceAddress api={api} onChanged={() => setAddressVersion(v => v + 1)}/>
      </div>
      : signedIn ? <div className="grid gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <p className="text-sm">Signed in as <strong dir="auto">{account?.email ?? email ?? "your account"}</strong>{host && <span className="text-muted-foreground"> on {host}</span>}</p>
          <LeaveControl busy={busy} onLeave={keepData => void leave(keepData)}/>
        </div>
        {syncApi && <SyncPanel api={syncApi}/>}
        {preview && <JoinPanel api={api} preview={preview} onDone={() => { setPreview(null); void loadAccount(); }}/>}
        {account && <>
          <p className="text-sm text-muted-foreground">Storage: {formatBytes(account.storageBytes)} of {formatBytes(account.storageQuotaBytes)} used.</p>
          <h4 className="text-sm font-semibold">Devices</h4>
          <ul className="grid gap-1.5" aria-label="Devices on this account">{account.devices.map(device => <DeviceRow key={device.id} device={device} busy={busy} onRemove={id => void removeDevice(id)}/>)}</ul>
        </>}
        {account && <DeleteAccountControl api={api} onDeleted={() => { setSignedIn(false); setEmail(null); setAccount(null); setPreview(null); }}/>}
        {!account && !busy && problem && !signInAgain && <div><Button type="button" size="sm" variant="outline" onClick={() => void loadAccount()}>Try again</Button></div>}
      </div>
      : <div className="flex flex-wrap items-center gap-2">
        <Button type="button" disabled={signingIn} onClick={() => void signIn()}><LogIn/>{signInAgain ? "Sign in again" : "Create an account or sign in"}</Button>
        {signingIn && <>
          <p role="status" className="text-sm text-muted-foreground">Finish signing in in your web browser, then come back here.</p>
          <Button type="button" size="sm" variant="ghost" onClick={() => void api.cancelSignIn({})}>Cancel</Button>
        </>}
        {!signingIn && <p className="w-full text-xs text-muted-foreground">Reason: sync with your other devices. Sign-in opens your web browser; Chaos never sees your email password.</p>}
        {!signingIn && <div className="w-full"><ServiceAddress api={api} onChanged={() => setAddressVersion(v => v + 1)}/></div>}
      </div>}
  </section>;
}
