import * as React from "react";
import { activityRpc, type ActivityEntryDto, type ActivityJobDto } from "#neoastra";

/**
 * The window's end of the system event stream (PLAN2 §6.4). The host pushes `activity.committed`
 * after a commit (coalesced to about 100 ms); this module then asks `activity.since` for what
 * changed, in order, and hands each subscriber the entries that touch its entity kinds.
 * `useActivity(kinds, handler)` is how a view learns that data it shows changed, and `useJobs()`
 * is how the sync indicators learn that a background job is running — neither polls.
 *
 * When subscribing fails (the page is not inside the desktop host) subscribers are told to reload
 * on window focus instead, which is what every view did before the stream existed.
 */
export type ActivityApi = Pick<typeof activityRpc, "since" | "head" | "jobs" | "onCommitted">;
export type ActivityReason = "entries" | "reset" | "focus";
export type ActivityHandler = (entries: readonly ActivityEntryDto[], reason: ActivityReason) => void;
type Subscriber = { kinds: ReadonlySet<string> | "*"; run: ActivityHandler };

const PAGE = 200;
const noJobs: Readonly<Record<string, ActivityJobDto>> = Object.freeze({});

class ActivityHub {
  private subscribers = new Set<Subscriber>();
  private jobListeners = new Set<() => void>();
  private jobUsers = 0;
  private jobsSnapshot: Readonly<Record<string, ActivityJobDto>> = noJobs;
  private after: string | null = null;
  private starting: Promise<void> | null = null;
  private unsubscribe: (() => void) | null = null;
  private pulling = false;
  private again = false;
  private disposed = false;
  failed = false;

  constructor(private readonly api: ActivityApi) {}

  get jobs() { return this.jobsSnapshot; }

  subscribe(subscriber: Subscriber) {
    this.subscribers.add(subscriber);
    void this.start();
    return () => { this.subscribers.delete(subscriber); };
  }

  subscribeJobs(listener: () => void) {
    this.jobListeners.add(listener);
    this.jobUsers++;
    void this.start().then(() => this.refreshJobs());
    return () => { this.jobListeners.delete(listener); this.jobUsers--; };
  }

  private start() {
    this.starting ??= (async () => {
      try {
        // Subscribe first, then read the head: anything committed in between is picked up by the first pull.
        this.unsubscribe = await this.api.onCommitted(() => this.schedule());
        const head = await this.api.head({});
        if (!head.ok) throw new Error("no head");
        this.after = head.head;
        await this.refreshJobs();
      } catch {
        this.failed = true;
      }
    })();
    return this.starting;
  }

  private schedule() {
    if (this.pulling) { this.again = true; return; }
    void this.pull();
  }

  private async pull() {
    if (this.disposed || this.after === null) return;
    this.pulling = true;
    try {
      do {
        this.again = false;
        let more = true;
        while (more && !this.disposed) {
          const page = await this.api.since({ after: this.after ?? "0", limit: PAGE });
          if (!page.ok) break;
          if (page.reset) {
            this.after = page.head;
            this.dispatch([], "reset");
            break;
          }
          if (page.entries.length > 0) {
            this.after = page.entries[page.entries.length - 1].seq;
            this.dispatch(page.entries, "entries");
          }
          more = page.entries.length >= PAGE;
        }
        if (this.jobUsers > 0) await this.refreshJobs();
      } while (this.again && !this.disposed);
    } catch {
      // The next push (or focus) retries from the same position.
    } finally {
      this.pulling = false;
    }
  }

  private dispatch(entries: readonly ActivityEntryDto[], reason: ActivityReason) {
    for (const subscriber of [...this.subscribers]) {
      if (reason === "reset") { subscriber.run([], reason); continue; }
      const touched = subscriber.kinds === "*" ? entries : entries.filter(entry => entry.entities.some(entity => (subscriber.kinds as ReadonlySet<string>).has(entity.kind)));
      if (touched.length > 0) subscriber.run(touched, reason);
    }
  }

  async refreshJobs() {
    try {
      const result = await this.api.jobs({});
      if (!result.ok || this.disposed) return;
      const next: Record<string, ActivityJobDto> = {};
      for (const job of result.jobs) next[job.job] = job;
      if (JSON.stringify(next) === JSON.stringify(this.jobsSnapshot)) return;
      this.jobsSnapshot = Object.freeze(next);
      this.jobListeners.forEach(listener => listener());
    } catch { /* the next push retries */ }
  }

  focusReload() {
    if (!this.failed) return;
    for (const subscriber of [...this.subscribers]) subscriber.run([], "focus");
  }

  dispose() {
    this.disposed = true;
    this.unsubscribe?.();
    this.subscribers.clear();
    this.jobListeners.clear();
  }
}

let hub: ActivityHub | null = null;
let configured: ActivityApi = activityRpc;

function current() { return hub ??= new ActivityHub(configured); }

/** Tests and the preview use their own transport; calling this drops the running hub. */
export function configureActivity(api: ActivityApi = activityRpc) {
  hub?.dispose();
  hub = null;
  configured = api;
}

/**
 * Calls <paramref name="handler"/> when entries touching one of <paramref name="kinds"/> (or any,
 * for "*") arrive, with every entry when the position was lost (reason "reset"), and on window
 * focus only while the push subscription is unavailable.
 */
export function useActivity(kinds: readonly string[] | "*", handler: ActivityHandler) {
  const handlerRef = React.useRef(handler); handlerRef.current = handler;
  const key = kinds === "*" ? "*" : [...kinds].sort().join("|");
  React.useEffect(() => {
    const active = current();
    const subscriber: Subscriber = { kinds: kinds === "*" ? "*" : new Set(kinds), run: (entries, reason) => handlerRef.current(entries, reason) };
    const release = active.subscribe(subscriber);
    function wake() { if (document.visibilityState === "visible") active.focusReload(); }
    window.addEventListener("focus", wake);
    document.addEventListener("visibilitychange", wake);
    return () => { release(); window.removeEventListener("focus", wake); document.removeEventListener("visibilitychange", wake); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
}

/** The latest state of each background job (running, last run and outcome), keyed by job name. */
export function useJobs(): Readonly<Record<string, ActivityJobDto>> {
  return React.useSyncExternalStore(subscribeJobs, jobsSnapshot, () => noJobs);
}

function subscribeJobs(listener: () => void) { return current().subscribeJobs(listener); }
function jobsSnapshot() { return current().jobs; }

/**
 * Calls <paramref name="handler"/> when the named job starts or finishes a run after this component
 * first saw it (the first value is the baseline, not a change). Replaces status polling while a sync runs.
 */
export function useJobChange(job: string, handler: () => void) {
  const state = useJobs()[job];
  const signature = state ? `${state.running}|${state.lastRunId ?? ""}` : null;
  const seen = React.useRef<string | null | undefined>(undefined);
  const handlerRef = React.useRef(handler); handlerRef.current = handler;
  React.useEffect(() => {
    if (seen.current === undefined) { seen.current = signature; return; }
    if (seen.current !== signature) { seen.current = signature; handlerRef.current(); }
  }, [signature]);
}
