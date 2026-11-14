import { GitHubSettings } from "./GitHubSettings";
import React from "react";
import { AppearanceSettings } from "./AppearanceSettings";
import { shellRpc, type ProfileSettingsResponse, type ShellStatusResponse, type ShortcutStatusDto } from "#neoastra";
import { Keyboard, MonitorCog, UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, Field, PageHeader } from "@/components/common";

type Api = typeof shellRpc;
export type SettingsLink = "notifications" | "summaries" | "myweek" | "data";

const dateFormats: { value: string; text: string }[] = [
  { value: "DMY", text: "Day / month / year (31/12/2026)" },
  { value: "MDY", text: "Month / day / year (12/31/2026)" },
  { value: "YMD", text: "Year / month / day (2026/12/31)" },
];

const stateText: Record<string, string> = { Registered: "Active", Off: "Off", Conflict: "In use elsewhere", Unsupported: "Not available", Failed: "Not registered" };

function Section({ title, icon, description, children }: { title: string; icon: React.ReactNode; description: React.ReactNode; children: React.ReactNode }) {
  return <section aria-label={title} className="grid gap-3 rounded-xl border bg-card p-4">
    <div className="grid gap-0.5">
      <h3 className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground">{icon}{title}</h3>
      <p className="text-sm text-muted-foreground">{description}</p>
    </div>
    {children}
  </section>;
}

function ShortcutState({ status }: { status: ShortcutStatusDto | undefined }) {
  if (!status) return null;
  const problem = status.state === "Conflict" || status.state === "Failed";
  return <p className={problem ? "text-xs text-destructive" : "text-xs text-muted-foreground"} role={problem ? "alert" : undefined}>
    {stateText[status.state] ?? status.state}: {status.message}
  </p>;
}

/**
 * Settings (PLAN §13): name, time zone and date format, the global capture and pad shortcuts
 * with whether Windows accepted them, keeping Chaos in the notification area, and Quit. Other
 * settings stay next to what they change and are linked from here.
 */
export function Settings({ api = shellRpc, onSaved, onNavigate, onGitHubSynced }: { api?: Api; onSaved: (zoneChanged: boolean) => void; onGitHubSynced?: () => void; onNavigate: (view: SettingsLink) => void }) {
  const [saved, setSaved] = React.useState<ProfileSettingsResponse | null>(null);
  const [draft, setDraft] = React.useState<ProfileSettingsResponse | null>(null);
  const [zones, setZones] = React.useState<string[]>([]);
  const [status, setStatus] = React.useState<ShellStatusResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [tab, setTab] = React.useState("general");

  async function load() {
    const [settings, zoneList, shell] = await Promise.all([api.settings({}), api.zones({}), api.status({})]);
    if (!settings.ok) throw new Error(settings.error?.message ?? "Could not load settings.");
    setSaved(settings); setDraft(settings); setZones([...zoneList.zones]); setStatus(shell);
  }
  React.useEffect(() => { void load().catch((e: unknown) => setError(e instanceof Error ? e.message : String(e))); }, [api]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!draft || !saved || busy) return;
    setBusy(true); setError(null); setNotice(null); setFieldErrors({});
    try {
      const result = await api.updateSettings({
        operationId: crypto.randomUUID(), expectedVersion: saved.version ?? "0",
        displayName: draft.displayName ?? "", zoneId: draft.zoneId ?? "", dateFormat: draft.dateFormat ?? "DMY",
        captureShortcut: draft.captureShortcut?.trim() || null, padShortcut: draft.padShortcut?.trim() || null,
        keepRunningInTray: draft.keepRunningInTray,
      });
      if (!result.ok) {
        const fields = result.error?.fields ?? [];
        setFieldErrors(Object.fromEntries(fields.map(field => [field.fieldPath, field.message])));
        if (fields.some(field => field.fieldPath === "captureShortcut" || field.fieldPath === "padShortcut")) setTab("shortcuts");
        else if (fields.length) setTab("general");
        throw new Error(result.error?.message ?? "Settings were not saved.");
      }
      setSaved(result); setDraft(result);
      setStatus(await api.status({}));
      setNotice(result.zoneChanged ? "Saved. Reminders were rescheduled for the new time zone." : "Saved.");
      onSaved(result.zoneChanged);
    } catch (e) { setError(e instanceof Error ? e.message : "Settings were not saved."); }
    finally { setBusy(false); }
  }

  const change = (patch: Partial<ProfileSettingsResponse>) => setDraft(current => current ? { ...current, ...patch } : current);
  const zoneOptions = draft?.zoneId && !zones.includes(draft.zoneId) ? [draft.zoneId, ...zones] : zones;
  const shortcut = (id: string) => status?.shortcuts.find(item => item.id === id);
  const dirty = !!draft && !!saved && JSON.stringify(draft) !== JSON.stringify(saved);

  return <div className="grid gap-5">
    <PageHeader eyebrow="Settings" title="Settings" subtitle="Personal preferences, GitHub connections and settings for this computer."/>
    {error && <Banner tone="error" onDismiss={() => setError(null)}>{error}</Banner>}
    {notice && <p role="status" className="text-sm text-muted-foreground">{notice}</p>}
    <Tabs value={tab} onValueChange={setTab} className="gap-4">
      <div className="max-w-full overflow-x-auto"><TabsList aria-label="Settings categories" className="justify-start">
        <TabsTrigger value="general">General</TabsTrigger><TabsTrigger value="appearance">Appearance</TabsTrigger><TabsTrigger value="shortcuts">Shortcuts</TabsTrigger><TabsTrigger value="computer">This computer</TabsTrigger><TabsTrigger value="github">GitHub</TabsTrigger><TabsTrigger value="more">More settings</TabsTrigger>
      </TabsList></div>
    {!draft ? <p className="text-sm text-muted-foreground">Loading settings…</p> : <form className="grid gap-5" onSubmit={save}>
      <TabsContent value="general"><Section title="You and your time" icon={<UserRound/>} description="The time zone decides when your day starts and when reminders arrive. Dates on tasks stay the dates you chose.">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Name"><Input dir="auto" value={draft.displayName ?? ""} aria-invalid={!!fieldErrors.displayName} onChange={e => change({ displayName: e.target.value })} maxLength={100}/></Field>
          <Field label="Time zone"><NativeSelect value={draft.zoneId ?? ""} aria-invalid={!!fieldErrors.zoneId} onChange={e => change({ zoneId: e.target.value })}>
            {zoneOptions.map(zone => <NativeSelectOption key={zone} value={zone}>{zone.replaceAll("_", " ")}</NativeSelectOption>)}
          </NativeSelect></Field>
          <Field label="Numeric dates in quick add" className="sm:col-span-2"><NativeSelect value={draft.dateFormat ?? "DMY"} onChange={e => change({ dateFormat: e.target.value })}>
            {dateFormats.map(format => <NativeSelectOption key={format.value} value={format.value}>{format.text}</NativeSelectOption>)}
          </NativeSelect></Field>
        </div>
      </Section></TabsContent>

      <TabsContent value="shortcuts"><Section title="Shortcuts from any app" icon={<Keyboard/>} description="Write a shortcut such as Ctrl+Alt+N, or leave it empty to turn it off. Inside Chaos, Ctrl+N and Ctrl+Shift+P always work.">
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="grid gap-1">
            <Field label="Quick add"><Input value={draft.captureShortcut ?? ""} placeholder="Off" aria-invalid={!!fieldErrors.captureShortcut} onChange={e => change({ captureShortcut: e.target.value })} autoComplete="off" spellCheck={false}/></Field>
            {fieldErrors.captureShortcut ? <p className="text-xs text-destructive">{fieldErrors.captureShortcut}</p> : <ShortcutState status={shortcut("capture")}/>}
          </div>
          <div className="grid gap-1">
            <Field label="New pad note"><Input value={draft.padShortcut ?? ""} placeholder="Off" aria-invalid={!!fieldErrors.padShortcut} onChange={e => change({ padShortcut: e.target.value })} autoComplete="off" spellCheck={false}/></Field>
            {fieldErrors.padShortcut ? <p className="text-xs text-destructive">{fieldErrors.padShortcut}</p> : <ShortcutState status={shortcut("pad")}/>}
          </div>
        </div>
      </Section></TabsContent>

      <TabsContent value="computer"><Section title="This computer" icon={<MonitorCog/>} description={status?.packaged
        ? "Installed version: reminders are handed to Windows and arrive even when Chaos is closed."
        : "Development run: reminders appear while Chaos is open. The installed version hands them to Windows."}>
        <label className="flex items-start gap-2 text-sm">
          <input type="checkbox" className="mt-0.5 accent-primary" checked={draft.keepRunningInTray} onChange={e => change({ keepRunningInTray: e.target.checked })}/>
          <span>Keep Chaos in the notification area when its window is closed, so reminders and summaries keep coming.
            {status && !status.trayAvailable && <span className="block text-xs text-muted-foreground">No notification-area icon is available right now, so closing the window quits Chaos.</span>}</span>
        </label>
        <p className="text-xs text-muted-foreground">Chaos {status?.appVersion ?? ""}</p>
      </Section></TabsContent>

      {tab !== "appearance" && tab !== "more" && tab !== "github" && <div className="flex flex-wrap items-center gap-2">
        <Button disabled={busy || !dirty}>Save settings</Button>
        <Button type="button" variant="outline" disabled={busy || !dirty} onClick={() => { setDraft(saved); setFieldErrors({}); }}>Undo changes</Button>
      </div>}
    </form>}

    <TabsContent value="appearance"><AppearanceSettings/></TabsContent>
    <TabsContent value="github"><GitHubSettings onSynced={onGitHubSynced}/></TabsContent>

    <TabsContent value="more"><nav aria-label="More settings" className="flex flex-wrap gap-2 text-sm">
      <Button type="button" variant="outline" size="sm" onClick={() => onNavigate("summaries")}>Summary times</Button>
      <Button type="button" variant="outline" size="sm" onClick={() => onNavigate("myweek")}>Week start and available time</Button>
    </nav></TabsContent>
    </Tabs>
  </div>;
}
