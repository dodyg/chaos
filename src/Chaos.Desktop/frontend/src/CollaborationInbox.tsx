import React from "react";
import { collaborationRpc, type CollaborationNotice } from "#neoastra";
import { Button } from "@/components/ui/button";
import { useActivity } from "./activity";

type Props = {
  onOpen: (notice: CollaborationNotice) => void;
  alerts?: boolean;
  api?: Pick<typeof collaborationRpc, "list" | "read">;
};
const noticeKinds = ["collaboration_notice"];

export function CollaborationInbox({ onOpen, alerts = false, api = collaborationRpc }: Props) {
  const [notices, setNotices] = React.useState<readonly CollaborationNotice[]>([]);
  const [error, setError] = React.useState("");
  const load = React.useCallback(async () => {
    try {
      const result = await api.list({});
      if (!result.ok) throw new Error(result.error?.message ?? "Could not load collaboration notices.");
      setNotices(result.notices);
      setError("");
    } catch (failure) {
      if (!alerts) setError(failure instanceof Error ? failure.message : "Could not load collaboration notices.");
    }
  }, [alerts, api]);
  React.useEffect(() => {
    void load();
  }, [load]);
  useActivity(noticeKinds, () => {
    void load();
  });

  async function read(notice: CollaborationNotice, open: boolean) {
    const result = await api.read({ id: notice.id });
    if (!result.ok) {
      setError(result.error?.message ?? "Could not mark this notice read.");
      return;
    }
    await load();
    if (open) onOpen(notice);
  }
  const visible = alerts ? notices.filter((notice) => notice.deliveredAtUtc && !notice.readAtUtc).slice(0, 3) : notices;
  if (alerts && visible.length === 0) return null;
  return (
    <section
      aria-label={alerts ? "Collaboration alerts" : "Collaboration inbox"}
      className={alerts ? "fixed right-4 bottom-4 z-40 grid max-w-sm gap-2" : "grid gap-3"}
    >
      {!alerts && <h2 className="text-xl font-semibold">Collaboration inbox</h2>}
      {error && <p role="alert">{error}</p>}
      {!alerts && visible.length === 0 && <p className="text-sm text-muted-foreground">No collaboration notices yet.</p>}
      {visible.map((notice) => (
        <article key={notice.id} className="grid gap-2 rounded-lg border bg-card p-3 shadow-sm">
          <p className="text-sm">
            {notice.kind === "due-soon" ? (
              "A shared task is due within a day"
            ) : (
              <>
                <strong>{notice.actorName}</strong>{" "}
                {notice.kind === "mentioned"
                  ? "mentioned you"
                  : notice.kind === "assigned"
                    ? "assigned you a task"
                    : notice.kind === "delegated"
                      ? "delegated a task to you"
                      : notice.kind === "delegation-response"
                        ? "answered about a task you delegated"
                        : notice.kind === "review-requested"
                          ? "reported a delegated task done for your review"
                          : notice.kind === "nudge"
                            ? "nudged you about a task"
                            : notice.kind.replaceAll("-", " ")}
              </>
            )}
            .
          </p>
          <p className="text-xs text-muted-foreground">
            {new Date(notice.createdAtUtc).toLocaleString()}
            {notice.readAtUtc ? " · Read" : ""}
          </p>
          <div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => void read(notice, true)}>
              Open
            </Button>
            {!notice.readAtUtc && (
              <Button size="sm" variant="ghost" onClick={() => void read(notice, false)}>
                Mark read
              </Button>
            )}
          </div>
        </article>
      ))}
    </section>
  );
}
