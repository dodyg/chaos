import type { ActivityEntryDto, ActivityJobDto } from "#neoastra";
import type { ActivityApi } from "./activity";

/** An in-memory activity feed for jsdom tests: push entries, change job state, then emit `activity.committed`. */
export function fakeActivity(options: { subscribeFails?: boolean } = {}) {
  let handler: ((value: { seq: string }) => void) | null = null;
  let seq = 0;
  let resetNext = false;
  const log: ActivityEntryDto[] = [];
  let jobs: ActivityJobDto[] = [];
  const calls = { since: 0, head: 0, jobs: 0, subscribed: 0 };
  const api: ActivityApi = {
    onCommitted: async (next: (value: { seq: string }) => void) => {
      if (options.subscribeFails) throw new Error("not in the desktop host");
      calls.subscribed++;
      handler = next;
      return () => { handler = null; };
    },
    head: async () => { calls.head++; return { ok: true, head: String(seq) }; },
    since: async ({ after }: { after: string; limit: number }) => {
      calls.since++;
      if (resetNext) { resetNext = false; return { ok: true, entries: [], head: String(seq), reset: true }; }
      const entries = log.filter(entry => Number(entry.seq) > Number(after));
      return { ok: true, entries, head: String(seq), reset: false };
    },
    jobs: async () => { calls.jobs++; return { ok: true, jobs }; },
  } as unknown as ActivityApi;
  return {
    api,
    calls,
    /** Adds one entry touching the given entity kinds. */
    push(kinds: string[], source = "schedule", action = "reminders.tick") {
      seq++;
      log.push({ seq: String(seq), operationId: `op${seq}`, occurredAtUtc: "2026-11-17T09:00:00Z", source, action, outcome: "ok", jobRunId: null,
        entities: kinds.map(kind => ({ kind, key: `${kind}-${seq}`, op: "upsert", version: "1" })) });
    },
    setJobs(next: Array<Partial<ActivityJobDto> & { job: string }>) {
      jobs = next.map(job => ({ running: false, runningSinceUtc: null, lastRunId: null, lastStartedUtc: null, lastFinishedUtc: null, lastOutcome: null, lastError: null, ...job }));
    },
    loseThePosition() { resetNext = true; },
    emit() { handler?.({ seq: String(seq) }); },
    get subscribed() { return handler !== null; },
  };
}
