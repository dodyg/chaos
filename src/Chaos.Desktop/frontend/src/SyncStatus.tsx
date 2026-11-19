import React from "react";
import { syncStatusRpc, type SyncStatusResponse } from "#neoastra";
import { AlertTriangle, Check, CloudOff, LogOut, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";

export type SyncApi = Pick<typeof syncStatusRpc, "status" | "syncNow" | "dismiss">;

const emptyStatus: SyncStatusResponse = { ok: true, kind: "Hidden", lastSyncedUtc: null, pending: 0, detail: null, attention: [] };

/** The local sync status, refreshed every few seconds. It reads the local database only, so it is cheap and never waits on the network. */
export function useSyncStatus(api: SyncApi = syncStatusRpc, intervalMs = 4000) {
  const [status, setStatus] = React.useState<SyncStatusResponse>(emptyStatus);
  const reload = React.useCallback(async () => {
    try {
      setStatus(await api.status({}));
    } catch {
      /* The host is not there (a preview, or closing): keep what was shown. */
    }
  }, [api]);
  React.useEffect(() => {
    void reload();
    const timer = setInterval(() => void reload(), intervalMs);
    return () => clearInterval(timer);
  }, [reload, intervalMs]);
  return { status, reload, setStatus };
}

/** "Synced 2 minutes ago", for the indicator and the Sync page. */
export function ago(instant: string | null | undefined, now = Date.now()): string {
  if (!instant) return "not yet";
  const seconds = Math.max(0, Math.round((now - new Date(instant).getTime()) / 1000));
  if (seconds < 10) return "just now";
  if (seconds < 60) return `${seconds} seconds ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return minutes === 1 ? "a minute ago" : `${minutes} minutes ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return hours === 1 ? "an hour ago" : `${hours} hours ago`;
  return new Date(instant).toLocaleDateString();
}

/** One line for each state, in the words the plan uses. */
export function statusLabel(status: SyncStatusResponse): string {
  switch (status.kind) {
    case "Synced":
      return status.lastSyncedUtc ? `Synced ${ago(status.lastSyncedUtc)}` : "Synced";
    case "Syncing":
      return "Syncing…";
    case "Offline":
      return status.pending > 0 ? `Offline — ${status.pending} ${status.pending === 1 ? "change" : "changes"} waiting` : "Offline";
    case "NeedsAttention":
      return `Needs attention (${status.attention.length})`;
    case "SignedOut":
      return "Signed out";
    case "UpdateNeeded":
      return "Update needed";
    default:
      return "";
  }
}

/** The short form for the sidebar header, where there is room for one word. The full text stays in the tooltip. */
export function shortLabel(status: SyncStatusResponse): string {
  switch (status.kind) {
    case "Synced":
      return "Synced";
    case "Syncing":
      return "Syncing…";
    case "Offline":
      return status.pending > 0 ? `Offline (${status.pending})` : "Offline";
    case "NeedsAttention":
      return `Attention (${status.attention.length})`;
    default:
      return statusLabel(status);
  }
}

const icons: Record<string, React.ReactNode> = {
  Synced: <Check />,
  Syncing: <RefreshCw className="animate-spin motion-reduce:animate-none" />,
  Offline: <CloudOff />,
  NeedsAttention: <AlertTriangle />,
  SignedOut: <LogOut />,
  UpdateNeeded: <AlertTriangle />,
};

/**
 * The small indicator in the sidebar footer (PLAN2 P15 item 3). It never blocks and is never modal; a computer that never used an
 * account shows nothing. Clicking opens Settings → Account, where the Sync page lives.
 */
export function SyncIndicator({ api = syncStatusRpc, onOpen, compact = false }: { api?: SyncApi; onOpen: () => void; compact?: boolean }) {
  const { status } = useSyncStatus(api);
  if (status.kind === "Hidden") return null;
  const label = compact ? shortLabel(status) : statusLabel(status);
  const warn = status.kind === "NeedsAttention" || status.kind === "UpdateNeeded" || status.kind === "SignedOut";
  return (
    <button
      type="button"
      data-sync-state={status.kind}
      title={status.detail ?? statusLabel(status)}
      onClick={onOpen}
      className={`flex min-w-0 items-center gap-2 rounded-md px-2 py-1 text-left text-xs outline-none hover:bg-sidebar-accent focus-visible:ring-[3px] focus-visible:ring-ring/50 [&_svg]:size-3.5 [&_svg]:shrink-0 ${warn ? "text-destructive" : "text-muted-foreground"}`}
    >
      {icons[status.kind]}
      <span role="status" className="truncate">
        {label}
      </span>
    </button>
  );
}

/** Settings → Account → Sync: what the indicator says, the pending count, attention items with their actions, and Sync now. */
export function SyncPanel({ api = syncStatusRpc }: { api?: SyncApi }) {
  const { status, setStatus } = useSyncStatus(api);
  const [busy, setBusy] = React.useState(false);
  if (status.kind === "Hidden") return null;

  async function run(action: () => Promise<SyncStatusResponse>) {
    setBusy(true);
    try {
      setStatus(await action());
    } catch {
      /* The next refresh shows the truth. */
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label="Sync" className="grid gap-2 rounded-md border p-3">
      <div className="flex flex-wrap items-center gap-2">
        <h4 className="text-sm font-semibold">Sync</h4>
        <span data-sync-state={status.kind} className="text-sm">
          {statusLabel(status)}
        </span>
        <Button
          type="button"
          size="sm"
          variant="outline"
          className="ml-auto"
          disabled={busy || status.kind === "SignedOut"}
          onClick={() => void run(() => api.syncNow({}))}
        >
          <RefreshCw />
          Sync now
        </Button>
      </div>
      {status.detail && <p className="text-sm text-muted-foreground">{status.detail}</p>}
      <dl className="grid gap-0.5 text-xs text-muted-foreground">
        <div className="flex gap-2">
          <dt>Last synced:</dt>
          <dd>{ago(status.lastSyncedUtc)}</dd>
        </div>
        <div className="flex gap-2">
          <dt>Waiting to upload:</dt>
          <dd>
            {status.pending} {status.pending === 1 ? "change" : "changes"}
          </dd>
        </div>
      </dl>
      {status.attention.length > 0 && (
        <ul aria-label="Needs attention" className="grid gap-1.5">
          {status.attention.map((item) => (
            <li key={item.id} data-attention={item.id} className="grid gap-1 rounded-md border p-2 text-sm">
              <p className="font-medium">{item.message}</p>
              <p className="text-xs text-muted-foreground">{item.suggestion}</p>
              <div className="flex flex-wrap gap-2">
                {item.actions.map((action) => (
                  <Button
                    key={action}
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={busy}
                    onClick={() => void run(() => (action === "Dismiss" ? api.dismiss({ id: item.id }) : api.syncNow({})))}
                  >
                    {action}
                  </Button>
                ))}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
