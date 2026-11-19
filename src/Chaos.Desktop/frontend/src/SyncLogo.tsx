import React from "react";
import { setSyncObserved, useSyncActivity } from "./syncActivity";
import { useJobs } from "./activity";

/**
 * The sidebar logo. While a sync runs it animates: GitHub spins an orbiting ring, Google Calendar (and feeds) flips the icon,
 * and both together wobble with a pulsing halo. Host-driven syncs are seen through the activity stream's job state (PLAN2 §6.4);
 * nothing polls.
 */
export function SyncLogo() {
  const { github, calendar } = useSyncActivity();
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
  const mode = github && calendar ? "both" : github ? "github" : calendar ? "calendar" : undefined;
  return (
    <span className="sync-logo" data-sync={mode} data-testid="sync-logo">
      <img src={new URL("../../../../eng/assets/chaos-icon.svg", import.meta.url).href} alt="" />
    </span>
  );
}
