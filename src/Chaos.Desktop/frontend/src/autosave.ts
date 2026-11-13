import React from "react";

/** Quiet time after the last keystroke before an edit is saved. */
export const autosaveDelayMs = 600;

export type SaveState = "saved" | "pending" | "saving" | "error";
type Versioned = { readonly id: string; readonly version: string };

/**
 * Saves a draft shortly after each change, one save at a time, so nothing is lost by closing
 * an editor or switching items. `save` gets the draft with the newest version this hook knows;
 * a failure keeps the draft (a newer change replaces it) and reports the message. Switching to
 * another item, or unmounting, saves what is waiting at once.
 */
export function useAutosave<T extends Versioned>({ currentId, save, validate, onSaved, onError }: {
  currentId: string | null;
  save: (draft: T) => Promise<{ saved: T } | { error: string }>;
  validate?: (draft: T) => string | null;
  onSaved: (saved: T) => void;
  onError: (message: string) => void;
}) {
  const [state, setState] = React.useState<SaveState>("saved");
  const pending = React.useRef<T | null>(null);
  const versions = React.useRef(new Map<string, number>());
  const timer = React.useRef<number | undefined>(undefined);
  const inFlight = React.useRef<Promise<void> | null>(null);
  const callbacks = React.useRef({ save, validate, onSaved, onError });
  callbacks.current = { save, validate, onSaved, onError };

  const flush = React.useCallback(async (): Promise<void> => {
    window.clearTimeout(timer.current);
    if (inFlight.current) await inFlight.current;
    const queued = pending.current;
    if (!queued) return;
    const problem = callbacks.current.validate?.(queued);
    if (problem) { setState("error"); callbacks.current.onError(problem); return; }
    pending.current = null;
    setState("saving");
    const draft = { ...queued, version: String(Math.max(Number(queued.version), versions.current.get(queued.id) ?? 0)) };
    const work = (async () => {
      let outcome: { saved: T } | { error: string };
      try { outcome = await callbacks.current.save(draft); }
      catch (e) { outcome = { error: e instanceof Error ? e.message : "The change could not be saved." }; }
      if ("error" in outcome) {
        pending.current ??= queued;
        setState("error");
        callbacks.current.onError(outcome.error);
        return;
      }
      const saved = outcome.saved;
      versions.current.set(saved.id, Number(saved.version));
      if (pending.current?.id === saved.id) pending.current = { ...pending.current, version: saved.version };
      callbacks.current.onSaved(saved);
      setState(pending.current ? "pending" : "saved");
    })();
    inFlight.current = work;
    try { await work; } finally { if (inFlight.current === work) inFlight.current = null; }
  }, []);

  const schedule = React.useCallback((draft: T) => {
    pending.current = draft;
    setState("pending");
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => void flush(), autosaveDelayMs);
  }, [flush]);

  React.useEffect(() => () => { if (currentId !== null && pending.current?.id === currentId) void flush(); }, [currentId, flush]);

  const label = state === "saving" ? "Saving…" : state === "pending" ? "Unsaved changes" : state === "error" ? "Not saved" : "All changes saved";
  return { state, label, flush, schedule, dirty: () => pending.current !== null || inFlight.current !== null };
}
