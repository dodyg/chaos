import React from "react";
import { setSyncObserved, useSyncActivity } from "./syncActivity";
import { useJobs } from "./activity";

/**
 * The sidebar logo. While a sync runs it animates: GitHub spins an orbiting ring, Google Calendar (and feeds) flips the icon,
 * and both together wobble with a pulsing halo. Host-driven syncs are seen through the activity stream's job state (PLAN2 §6.4);
 * nothing polls.
 */
export function SyncLogo() {
  const { calendar } = useSyncActivity();
  const jobs = useJobs();
  const githubRunning = jobs["github.sync"]?.running === true;
  const calendarRunning = jobs["calendar.refresh"]?.running === true;
  React.useEffect(() => {
    setSyncObserved("github", githubRunning);
  }, [githubRunning]);
  React.useEffect(() => {
    setSyncObserved("calendar", calendarRunning);
  }, [calendarRunning]);
  React.useEffect(
    () => () => {
      setSyncObserved("github", false);
      setSyncObserved("calendar", false);
    },
    [],
  );
  // GitHub syncs often in the background and its ring was distracting; its progress shows per repository in GitHub settings instead.
  const mode = calendar ? "calendar" : undefined;
  return (
    <span className="sync-logo" data-sync={mode} data-testid="sync-logo">
      <img src={new URL("../../../../eng/assets/chaos-icon.svg", import.meta.url).href} alt="" />
    </span>
  );
}
