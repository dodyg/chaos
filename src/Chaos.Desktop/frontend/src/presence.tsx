import React from "react";
import { presenceRpc, type PresenceMemberResponse } from "#neoastra";

/** PLAN3 9.9: report every 20 seconds while a shared note or page is open; ask who else has it open a little more often. */
export const presenceReportMs = 20_000;
export const presenceWhoMs = 2_000;

export type PresenceApi = Pick<typeof presenceRpc, "report" | "leave" | "who">;

/** Presence is a nicety: outside the desktop host, or when a call fails, nothing is shown and nothing breaks. */
function attempt<T>(call: () => Promise<T>): Promise<T | undefined> {
  try {
    return call().catch(() => undefined);
  } catch {
    return Promise.resolve(undefined);
  }
}

/**
 * Tells the host that this shared entity is open (and whether it is being written to), leaves on close, and returns the other
 * people who have it open. The host sends nothing for the Personal space, so a Personal note simply shows nobody.
 */
export function usePresence(
  spaceId: string | null | undefined,
  kind: "pad" | "page" | "task",
  entityId: string | null | undefined,
  editing: boolean,
  api: PresenceApi = presenceRpc,
): readonly PresenceMemberResponse[] {
  const [members, setMembers] = React.useState<readonly PresenceMemberResponse[]>([]);
  const editingRef = React.useRef(editing);
  editingRef.current = editing;

  const report = React.useCallback(
    (state: boolean) => {
      if (!spaceId || !entityId) return Promise.resolve();
      return attempt(() => api.report({ spaceId, kind, entityId, editing: state })).then((result) => {
        if (result) setMembers(result.members);
      });
    },
    [api, spaceId, kind, entityId],
  );

  React.useEffect(() => {
    if (!spaceId || !entityId) {
      setMembers([]);
      return;
    }
    let live = true;
    void report(editingRef.current);
    const heartbeat = window.setInterval(() => void report(editingRef.current), presenceReportMs);
    const poll = window.setInterval(() => {
      void attempt(() => api.who({ spaceId, kind, entityId })).then((result) => {
        if (live && result) setMembers(result.members);
      });
    }, presenceWhoMs);
    return () => {
      live = false;
      window.clearInterval(heartbeat);
      window.clearInterval(poll);
      setMembers([]);
      void attempt(() => api.leave({ spaceId, kind, entityId }));
    };
  }, [api, report, spaceId, kind, entityId]);

  // Writing starts or stops: say so at once instead of at the next heartbeat.
  React.useEffect(() => {
    void report(editing);
  }, [editing, report]);

  return members;
}

/** "Omar is editing", "Omar and Sara are viewing", "Omar is editing; Sara is viewing". */
export function presenceText(members: readonly PresenceMemberResponse[]): string {
  if (members.length === 0) return "";
  const names = (list: readonly PresenceMemberResponse[]) => {
    const all = list.map((member) => member.displayName);
    return all.length <= 2 ? all.join(" and ") : `${all.slice(0, -1).join(", ")} and ${all[all.length - 1]}`;
  };
  const editing = members.filter((member) => member.editing);
  const viewing = members.filter((member) => !member.editing);
  const parts: string[] = [];
  if (editing.length > 0) parts.push(`${names(editing)} ${editing.length === 1 ? "is" : "are"} editing`);
  if (viewing.length > 0) parts.push(`${names(viewing)} ${viewing.length === 1 ? "is" : "are"} viewing`);
  return parts.join("; ");
}

export function PresenceLine({ members }: { members: readonly PresenceMemberResponse[] }) {
  const text = presenceText(members);
  if (!text) return null;
  return (
    <p role="status" aria-live="polite" className="text-xs text-muted-foreground">
      {text}
    </p>
  );
}
