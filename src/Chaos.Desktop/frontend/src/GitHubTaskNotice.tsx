import React from "react";
import { gitHubRpc, type GitHubTaskSyncResponse } from "#neoastra";

/** Local metadata only: opening a task never starts authentication or a sync pass. */
export function GitHubTaskNotice({ taskId, version, revision, api = gitHubRpc }: { taskId: string; version: string; revision: number; api?: typeof gitHubRpc }) {
  const [info, setInfo] = React.useState<GitHubTaskSyncResponse | null>(null);
  const [failed, setFailed] = React.useState(false);
  React.useEffect(() => {
    let current = true;
    setInfo(null); setFailed(false);
    void api.taskSync({ taskId }).then(result => { if (current) { setInfo(result); setFailed(!result.ok); } })
      .catch(() => { if (current) setFailed(true); });
    return () => { current = false; };
  }, [api, taskId, version, revision]);
  if (failed) return <p role="status" className="text-xs text-muted-foreground">Could not check GitHub sync status.</p>;
  if (!info?.connected) return null;
  return <div role="status" aria-label="GitHub sync status" className="grid gap-1 rounded-lg border bg-muted/50 px-3 py-2 text-xs">
    <span>GitHub · {info.state}</span>
    {info.pending && <strong>Not sent to GitHub yet.</strong>}
    <span className="text-muted-foreground">Only incoming changes are synced right now. Local edits are not sent to GitHub yet.</span>
  </div>;
}
