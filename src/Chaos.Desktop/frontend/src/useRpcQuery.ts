import React from "react";
import { useActivity } from "./activity";

export type RpcQuery<T> = {
  /** The latest successful result; the previous one stays while a reload runs or fails. */
  data: T | undefined;
  /** The message of the last failed load, cleared by the next success. */
  error: string | null;
  /** Loads again now; resolves once this load has settled (even if a newer one superseded it). */
  reload: () => Promise<void>;
};

/**
 * Loads data through an RPC and reloads it when activity of the given kinds arrives or <paramref name="deps"/> change.
 * A response that arrives after a newer load started (or after unmount) is dropped, so a slow answer never overwrites a fresh one.
 * <paramref name="load"/> returns the data, or throws; an `ok: false` response should throw an Error with its message.
 */
export function useRpcQuery<T>(
  load: () => Promise<T>,
  deps: React.DependencyList,
  activityKinds: readonly string[] | "*" = [],
): RpcQuery<T> {
  const [data, setData] = React.useState<T | undefined>(undefined);
  const [error, setError] = React.useState<string | null>(null);
  const latest = React.useRef(0);
  const loadRef = React.useRef(load);
  loadRef.current = load;

  const reload = React.useCallback(async () => {
    const mine = ++latest.current;
    try {
      const result = await loadRef.current();
      if (mine === latest.current) {
        setData(result);
        setError(null);
      }
    } catch (e) {
      if (mine === latest.current) setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  React.useEffect(() => {
    void reload();
    // A counter, not a DOM node: bumping it on cleanup is what drops a response that arrives after unmount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    return () => {
      latest.current++;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reload, ...deps]);
  useActivity(activityKinds, () => void reload());
  return { data, error, reload };
}
