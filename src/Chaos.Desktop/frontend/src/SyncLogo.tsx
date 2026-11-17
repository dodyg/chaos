import React from "react";
import { calendarsRpc, gitHubRpc } from "#neoastra";
import { setSyncObserved, useSyncActivity } from "./syncActivity";

/**
 * The sidebar logo. While a sync runs it animates: GitHub spins an orbiting ring, Google Calendar (and feeds) flips the icon,
 * and both together wobble with a pulsing halo. Host-driven GitHub syncs are noticed by a light status poll.
 */
export function SyncLogo({ pollMs = 4000 }: { pollMs?: number }) {
  const { github, calendar } = useSyncActivity();
  React.useEffect(() => {
    let stopped = false;
    async function check() {
      if (stopped || document.visibilityState === "hidden") return;
      try { const result = await gitHubRpc.status({}); if (!stopped && result.ok) setSyncObserved("github", result.sync?.running === true); }
      catch { /* outside the desktop host: nothing to observe */ }
      try { const result = await calendarsRpc.activity({}); if (!stopped && result.ok) setSyncObserved("calendar", result.google || result.feed); }
      catch { /* outside the desktop host: nothing to observe */ }
    }
    void check();
    const timer = window.setInterval(() => void check(), pollMs);
    return () => { stopped = true; window.clearInterval(timer); };
  }, [pollMs]);
  const mode = github && calendar ? "both" : github ? "github" : calendar ? "calendar" : undefined;
  return <span className="sync-logo" data-sync={mode} data-testid="sync-logo">
    <img src={new URL("../../../../eng/assets/chaos-icon.svg", import.meta.url).href} alt=""/>
  </span>;
}
