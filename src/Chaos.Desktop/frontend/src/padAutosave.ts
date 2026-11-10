/**
 * Revision-aware autosave for pad notes (PLAN §9). Every edit gets the next editor revision;
 * a save is sent about 500 ms after typing stops, or at once on flush (blur, navigation,
 * close). Only one save is in flight per note: edits made meanwhile go out in the next save
 * with a newer revision, and the store ignores any revision it has already acknowledged, so
 * a slow or repeated request can never replace newer text. A failed save keeps the text and
 * stays Failed until a later save succeeds; nothing here discards a draft.
 */

export type SaveStatus = "saved" | "pending" | "saving" | "failed";

export type SaveOutcome =
  | { ok: true; draftVersion: number; version: string; markdown: string }
  | { ok: false; message: string };

export type Scheduler = { set: (callback: () => void, ms: number) => unknown; clear: (handle: unknown) => void };

export const browserScheduler: Scheduler = {
  set: (callback, ms) => setTimeout(callback, ms),
  clear: handle => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export type DraftSnapshot = {
  itemId: string;
  text: string;
  revision: number;
  acknowledged: number;
  status: SaveStatus;
  error: string | null;
  /** The note's row version after the latest acknowledged save, for later archive/delete calls. */
  version: string;
};

export type DraftSaverOptions = {
  itemId: string;
  text: string;
  /** The note's DraftVersion as loaded: new edits continue from it. */
  revision: number;
  version: string;
  save: (revision: number, text: string) => Promise<SaveOutcome>;
  delayMs?: number;
  scheduler?: Scheduler;
};

export class DraftSaver {
  readonly itemId: string;
  private text: string;
  private revision: number;
  private acknowledged: number;
  private status: SaveStatus = "saved";
  private error: string | null = null;
  private version: string;
  private timer: unknown = null;
  private flushing: Promise<boolean> | null = null;
  private readonly listeners = new Set<(snapshot: DraftSnapshot) => void>();
  private readonly save: DraftSaverOptions["save"];
  private readonly delayMs: number;
  private readonly scheduler: Scheduler;

  constructor(options: DraftSaverOptions) {
    this.itemId = options.itemId;
    this.text = options.text;
    this.revision = options.revision;
    this.acknowledged = options.revision;
    this.version = options.version;
    this.save = options.save;
    this.delayMs = options.delayMs ?? 500;
    this.scheduler = options.scheduler ?? browserScheduler;
  }

  get snapshot(): DraftSnapshot {
    return { itemId: this.itemId, text: this.text, revision: this.revision, acknowledged: this.acknowledged, status: this.status, error: this.error, version: this.version };
  }

  /** True while some typed text is not acknowledged by the store yet. */
  get dirty(): boolean { return this.acknowledged < this.revision; }

  subscribe(listener: (snapshot: DraftSnapshot) => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /** Records a new text and schedules a save after the idle delay. */
  edit(text: string): void {
    if (text === this.text) return;
    this.text = text;
    this.revision += 1;
    if (this.status !== "saving" && this.status !== "failed") this.status = "pending";
    this.schedule();
    this.notify();
  }

  /** The row version changed through another command (archive, tag, …). */
  updateVersion(version: string): void {
    this.version = version;
    this.notify();
  }

  /**
   * Saves everything typed so far now. Resolves true once the store acknowledged the latest
   * revision, false when a save failed (the text is kept; call flush again to retry).
   */
  flush(): Promise<boolean> {
    if (this.timer !== null) { this.scheduler.clear(this.timer); this.timer = null; }
    this.flushing ??= this.run().finally(() => { this.flushing = null; });
    return this.flushing;
  }

  dispose(): void {
    if (this.timer !== null) { this.scheduler.clear(this.timer); this.timer = null; }
    this.listeners.clear();
  }

  private schedule() {
    if (this.timer !== null) this.scheduler.clear(this.timer);
    this.timer = this.scheduler.set(() => { this.timer = null; void this.flush(); }, this.delayMs);
  }

  private async run(): Promise<boolean> {
    while (this.acknowledged < this.revision) {
      const revision = this.revision;
      const text = this.text;
      this.status = "saving";
      this.notify();
      let outcome: SaveOutcome;
      try { outcome = await this.save(revision, text); }
      catch (error) { outcome = { ok: false, message: error instanceof Error ? error.message : "The note could not be saved." }; }
      if (!outcome.ok) {
        this.status = "failed";
        this.error = outcome.message;
        this.notify();
        return false;
      }
      // The store answers with the revision it holds: this one, or a newer one when this
      // request was a repeat. Anything older means the text did not land.
      if (outcome.draftVersion < revision) {
        this.status = "failed";
        this.error = "The note was not saved. Try again.";
        this.notify();
        return false;
      }
      this.acknowledged = Math.max(this.acknowledged, revision);
      this.version = outcome.version;
      this.error = null;
    }
    this.status = "saved";
    this.notify();
    return true;
  }

  private notify() {
    const snapshot = this.snapshot;
    for (const listener of this.listeners) listener(snapshot);
  }
}

/**
 * Savers by note ID, owned above the pad view so a draft (and a failed save) survives
 * switching views: navigation flushes, it never discards.
 */
export class DraftRegistry {
  private readonly savers = new Map<string, DraftSaver>();
  private readonly listeners = new Set<() => void>();

  get(itemId: string): DraftSaver | undefined { return this.savers.get(itemId); }

  getOrCreate(options: DraftSaverOptions): DraftSaver {
    let saver = this.savers.get(options.itemId);
    if (!saver) {
      saver = new DraftSaver(options);
      saver.subscribe(() => this.notify());
      this.savers.set(options.itemId, saver);
    }
    return saver;
  }

  /** Drops a saver that holds nothing unsaved (e.g. after its note was deleted). */
  forget(itemId: string): void {
    const saver = this.savers.get(itemId);
    if (saver && !saver.dirty) { saver.dispose(); this.savers.delete(itemId); }
  }

  get dirty(): boolean { return [...this.savers.values()].some(saver => saver.dirty); }

  get failed(): DraftSaver[] { return [...this.savers.values()].filter(saver => saver.snapshot.status === "failed"); }

  /** Saves every note with unsaved text; true when all of them are acknowledged. */
  async flushAll(): Promise<boolean> {
    const results = await Promise.all([...this.savers.values()].filter(saver => saver.dirty).map(saver => saver.flush()));
    return results.every(Boolean);
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private notify() { for (const listener of this.listeners) listener(); }
}
