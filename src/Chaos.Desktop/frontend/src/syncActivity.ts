import * as React from "react";

/** Which background syncs are running right now; the sidebar logo animates from this. */
export type SyncKind = "github" | "calendar";
export type SyncActivity = { github: boolean; calendar: boolean };

const counts: Record<SyncKind, number> = { github: 0, calendar: 0 };
const observed: Record<SyncKind, boolean> = { github: false, calendar: false };
const listeners = new Set<() => void>();
let snapshot: SyncActivity = { github: false, calendar: false };

function publish() {
  const next = { github: counts.github > 0 || observed.github, calendar: counts.calendar > 0 || observed.calendar };
  if (next.github === snapshot.github && next.calendar === snapshot.calendar) return;
  snapshot = next;
  listeners.forEach((listener) => listener());
}

/** Marks a sync as running until the promise settles. Returns the promise unchanged. */
export function trackSync<T>(kind: SyncKind, work: Promise<T>): Promise<T> {
  counts[kind]++;
  publish();
  const done = () => {
    counts[kind] = Math.max(0, counts[kind] - 1);
    publish();
  };
  work.then(done, done);
  return work;
}

/** For syncs the host runs on its own and we only see by polling. */
export function setSyncObserved(kind: SyncKind, running: boolean) {
  observed[kind] = running;
  publish();
}

export function useSyncActivity(): SyncActivity {
  return React.useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    () => snapshot,
    () => snapshot,
  );
}
