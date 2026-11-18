import React from "react";
import { accountRpc, type AccountErrorDto, type AccountInfoDto, type ServiceAddressResponse } from "#neoastra";
import { CloudOff, LogIn, LogOut, MonitorSmartphone, UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { InlineError } from "@/components/common";
import { Input } from "@/components/ui/input";

type Api = Pick<typeof accountRpc, "status" | "account" | "signIn" | "cancelSignIn" | "signOut" | "revokeDevice" | "address" | "setAddress">;

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
export function AccountSettings({ api = accountRpc }: { api?: Api }) {
  const [configured, setConfigured] = React.useState(true);
  const [signedIn, setSignedIn] = React.useState<boolean | null>(null);
  const [email, setEmail] = React.useState<string | null>(null);
  const [host, setHost] = React.useState<string | null>(null);
  const [account, setAccount] = React.useState<AccountInfoDto | null>(null);
  const [problem, setProblem] = React.useState<AccountErrorDto | null>(null);
  const [signingIn, setSigningIn] = React.useState(false);
  const [busy, setBusy] = React.useState(false);

  const fail = React.useCallback((error: AccountErrorDto | null | undefined, fallback: string) => {
    const next = error ?? { kind: "Other", message: fallback };
    setProblem(next);
    if (next.kind === "SignInAgain") { setSignedIn(false); setAccount(null); }
  }, []);

  const loadAccount = React.useCallback(async () => {
    setBusy(true);
    try {
      const result = await api.account({});
      if (result.ok && result.account) { setAccount(result.account); setEmail(result.account.email); setProblem(null); } else fail(result.error, "Could not load your account.");
    } catch (e) { fail(null, String(e)); } finally { setBusy(false); }
  }, [api, fail]);

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
      if (result.ok && result.account) { setAccount(result.account); setEmail(result.account.email); setSignedIn(true); } else fail(result.error, "Sign-in did not finish.");
    } catch (e) { fail(null, String(e)); } finally { setSigningIn(false); }
  }

  async function signOut() {
    setBusy(true); setProblem(null);
    try {
      const result = await api.signOut({});
      if (result.ok) { setSignedIn(result.signedIn); setEmail(null); setAccount(null); } else fail(result.error, "Could not sign out.");
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
          <Button type="button" size="sm" variant="outline" className="ml-auto" disabled={busy} onClick={() => void signOut()}><LogOut/>Sign out</Button>
        </div>
        {account && <>
          <p className="text-sm text-muted-foreground">Storage: {formatBytes(account.storageBytes)} of {formatBytes(account.storageQuotaBytes)} used.</p>
          <h4 className="text-sm font-semibold">Devices</h4>
          <ul className="grid gap-1.5" aria-label="Devices on this account">{account.devices.map(device => <DeviceRow key={device.id} device={device} busy={busy} onRemove={id => void removeDevice(id)}/>)}</ul>
        </>}
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
